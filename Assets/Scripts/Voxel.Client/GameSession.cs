using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Starts and stops a game: hosting (server and local player), joining a server, or running
    // as a dedicated server, with no local player and no display.
    //
    // Command line arguments:
    //   -server               dedicated server (implied by -batchmode)
    //   -host                 hosts a game at launch
    //   -connect <address>    joins a server at launch
    //   -port <port>          UDP port, 7777 by default
    //   -password <password>  password required by the server, or presented by the client
    //   -data <directory>     folder of the server's saves
    //   -autosave <seconds>   interval of the server's automatic saves
    //   -playerid <id>        player id, instead of the one stored on this machine
    //   -timescale <factor>   simulation speed when the server starts, 1 by default
    //   -admin <id,id,...>    ids of the players allowed to set the simulation speed
    //   -worldsize <x,y,z>    server world size in chunks, instead of the one set in the scene
    //   -noupnp               the server does not ask the router to forward its port
    public class GameSession : MonoBehaviour
    {
        private const string LocalAddress = "127.0.0.1";
        private const string AnyAddress = "0.0.0.0";
        private const int MaxAddressLength = 15;
        private const int MaxPasswordLength = 32;

        // The router forgets a forwarded port after two hours: it is asked again every hour.
        private const float ForwardingRenewalInterval = 3600f;

        // Longest wait, when the game closes, for the router to remove the forwarded port.
        private const int ForwardingCloseTimeout = 4000;

        // Simulation speeds cycled through by the F5 and F6 keys. Zero pauses time.
        private static readonly float[] TimeScales = { 0f, 1f, 10f, 100f, 1000f, 10000f };

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private VoxelWorld voxelWorld;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private ushort port = 7777;
        [SerializeField] private float autosaveInterval = 60f;
        [SerializeField] private bool openRouterPort = true;

        private string address = LocalAddress;
        private string password = "";
        private string playerId;
        private string dataDirectory;
        private float startTimeScale = 1f;
        private string[] adminIds = Array.Empty<string>();
        private Vector3Int? worldSize;
        private string message = "";
        private bool editingPassword;
        private bool sessionActive;
        private bool leaving;
        private PlayerRegistry registry;
        private float nextAutosave;

        // Port forwarding on the router (server role only): being asked for, then obtained or refused,
        // then being removed. The router is only ever talked to from background threads.
        private Task<PortForwarding> forwardingRequest;
        private PortForwarding forwarding;
        private Task forwardingRemoval;
        private float nextForwardingRenewal;
        private bool quitting;
        private Keyboard textInputKeyboard;
        private GUIStyle textStyle;

        private UnityTransport Transport => (UnityTransport)networkManager.NetworkConfig.NetworkTransport;

        private void Start()
        {
            networkManager.OnClientStopped += OnSessionStopped;
            networkManager.OnServerStopped += OnSessionStopped;

            string[] arguments = Environment.GetCommandLineArgs();
            ReadSettings(arguments);

            if (Array.IndexOf(arguments, "-server") >= 0 || Application.isBatchMode)
            {
                StartDedicatedServer();
            }
            else if (Array.IndexOf(arguments, "-host") >= 0)
            {
                StartHost();
            }
            else if (ArgumentValue(arguments, "-connect") != null)
            {
                address = ArgumentValue(arguments, "-connect");
                StartClient();
            }
        }

        private void OnDestroy()
        {
            ListenToTextInput(null);
            if (networkManager != null)
            {
                networkManager.OnClientStopped -= OnSessionStopped;
                networkManager.OnServerStopped -= OnSessionStopped;
            }
        }

        private void OnApplicationQuit()
        {
            // Closing the game is a deliberate stop: the end of session that follows is not an error to report.
            leaving = true;
            SaveSession();

            // Background work is cut short when the game closes: the router is given time to answer.
            quitting = true;
            CloseRouterPort();
        }

        private void Update()
        {
            TrackRouterPort();

            if (registry != null)
            {
                registry.TrackPlayers();
                if (Time.unscaledTime >= nextAutosave)
                {
                    nextAutosave = Time.unscaledTime + autosaveInterval;
                    SaveSession();
                }
            }

            Keyboard keyboard = Keyboard.current;
            ListenToTextInput(keyboard);
            if (keyboard == null || networkManager.ShutdownInProgress) return;

            if (networkManager.IsListening)
            {
                if (keyboard.f10Key.wasPressedThisFrame)
                {
                    // Players disappear during shutdown: their position is recorded before.
                    SaveSession();
                    leaving = true;
                    networkManager.Shutdown();
                }
                else if (keyboard.f5Key.wasPressedThisFrame)
                {
                    ecosystemSync.RequestMultiplier(NextTimeScale(-1));
                }
                else if (keyboard.f6Key.wasPressedThisFrame)
                {
                    ecosystemSync.RequestMultiplier(NextTimeScale(1));
                }
                else if (keyboard.f7Key.wasPressedThisFrame)
                {
                    ecosystemSync.RequestAdvanceDays(1);
                }

                return;
            }

            if (keyboard.tabKey.wasPressedThisFrame)
            {
                editingPassword = !editingPassword;
            }

            if (keyboard.backspaceKey.wasPressedThisFrame)
            {
                if (editingPassword) password = WithoutLastCharacter(password);
                else address = WithoutLastCharacter(address);
            }

            if (keyboard.f1Key.wasPressedThisFrame)
            {
                StartHost();
            }
            else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
            {
                StartClient();
            }
        }

        private void OnGUI()
        {
            string text = ScreenText();
            if (text.Length == 0) return;

            if (textStyle == null)
            {
                textStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
            }

            // Text doubled with a shadow to stay readable on the sky as well as on the terrain.
            var area = new Rect(20f, 20f, Screen.width - 40f, Screen.height - 40f);
            textStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), text, textStyle);
            textStyle.normal.textColor = Color.white;
            GUI.Label(area, text, textStyle);
        }

        public void StartHost()
        {
            StartServerSession(true);
        }

        public void StartDedicatedServer()
        {
            StartServerSession(false);
        }

        public void StartClient()
        {
            message = "";
            leaving = false;
            if (address.Split('.').Length != 4 || !IPAddress.TryParse(address, out _))
            {
                message = $"'{address}' is not a valid IPv4 address.";
                return;
            }

            networkManager.NetworkConfig.ConnectionData = OwnConnectionRequest();
            Transport.SetConnectionData(address, port);
            if (!networkManager.StartClient())
            {
                message = $"Could not start the connection to {address}.";
                return;
            }

            // The client's world is created by WorldSync from what the server sends.
            worldSync.StartClient();
            ecosystemSync.StartClient();
            inventorySync.StartClient();
            sessionActive = true;
        }

        private void StartServerSession(bool withLocalPlayer)
        {
            message = "";
            leaving = false;

            Directory.CreateDirectory(dataDirectory);
            voxelWorld.LoadPersistent(dataDirectory, worldSize);
            registry = new PlayerRegistry(networkManager, voxelWorld.World, dataDirectory, password);

            // The server listens on every interface so it can be reached from other machines.
            Transport.SetConnectionData(LocalAddress, port, AnyAddress);

            bool started;
            if (withLocalPlayer)
            {
                networkManager.NetworkConfig.ConnectionData = OwnConnectionRequest();
                started = networkManager.StartHost();
            }
            else
            {
                // Without a display, nothing limits the frame rate: it is set to the network's.
                Application.targetFrameRate = (int)networkManager.NetworkConfig.TickRate;
                started = networkManager.StartServer();
            }

            if (!started)
            {
                EndSession();
                message = $"Could not start a server on port {port}.";
                Debug.LogError(message);
                QuitIfDedicated();
                return;
            }

            worldSync.StartServer();
            ecosystemSync.StartServer(voxelWorld.World, dataDirectory, startTimeScale, IsAdmin);
            inventorySync.StartServer(registry);
            sessionActive = true;
            nextAutosave = Time.unscaledTime + autosaveInterval;
            Debug.Log($"Server started on port {port}. Saves in {dataDirectory}, every {autosaveInterval} s.");
            OpenRouterPort();
        }

        // Asks the router to forward the server's port, so that players outside the local network can
        // join without configuring it by hand. The answer takes a few seconds: TrackRouterPort reports it.
        private void OpenRouterPort()
        {
            if (!openRouterPort) return;

            int forwardedPort = port;
            string description = Application.productName;

            // A removal still under way from the previous session must not land after this request.
            Task removal = forwardingRemoval;
            forwardingRequest = Task.Run(() =>
            {
                removal?.Wait();
                return PortForwarding.Open(forwardedPort, description);
            });
        }

        private void TrackRouterPort()
        {
            if (forwardingRequest != null && forwardingRequest.IsCompleted)
            {
                forwarding = forwardingRequest.Result;
                forwardingRequest = null;
                nextForwardingRenewal = Time.unscaledTime + ForwardingRenewalInterval;

                if (forwarding.IsReachable) Debug.Log(forwarding.Summary);
                else Debug.LogWarning(forwarding.Summary);
            }
            else if (forwarding != null && forwarding.IsOpen && Time.unscaledTime >= nextForwardingRenewal)
            {
                nextForwardingRenewal = Time.unscaledTime + ForwardingRenewalInterval;
                PortForwarding renewed = forwarding;
                Task.Run(() => renewed.Renew());
            }
        }

        // Removes the forwarded port from the router, without holding the game up unless it is closing.
        private void CloseRouterPort()
        {
            Task<PortForwarding> request = forwardingRequest;
            PortForwarding opened = forwarding;
            forwardingRequest = null;
            forwarding = null;

            if (request != null) forwardingRemoval = request.ContinueWith(finished => finished.Result.Close());
            else if (opened != null) forwardingRemoval = Task.Run(() => opened.Close());

            if (quitting && forwardingRemoval != null)
            {
                forwardingRemoval.Wait(ForwardingCloseTimeout);
                forwardingRemoval = null;
            }
        }

        // Writes the world, the ecosystem and the players to disk. No effect outside the server role.
        private void SaveSession()
        {
            if (registry == null) return;

            registry.Save();
            voxelWorld.Save();
            ecosystemSync.Save();
        }

        // The host is admin of its own game; on a dedicated server, admins are the players
        // named by the -admin argument.
        private bool IsAdmin(ulong clientId)
        {
            if (networkManager.IsHost && clientId == networkManager.LocalClientId) return true;

            return registry != null
                && registry.TryGetPlayerId(clientId, out string id)
                && Array.IndexOf(adminIds, id) >= 0;
        }

        // Next or previous speed in the list, starting from the current speed.
        private float NextTimeScale(int direction)
        {
            int current = 0;
            for (int i = 0; i < TimeScales.Length; i++)
            {
                if (TimeScales[i] <= ecosystemSync.Multiplier) current = i;
            }

            return TimeScales[Mathf.Clamp(current + direction, 0, TimeScales.Length - 1)];
        }

        private void EndSession()
        {
            // Last moment when the state of the game still exists. When the game closes, the network can
            // stop before SaveSession has been called: so we save here too.
            ecosystemSync.Save();
            ecosystemSync.Stop();
            inventorySync.Stop();
            worldSync.Stop();
            if (registry != null)
            {
                registry.Save();
                registry.Close();
                registry = null;
            }

            voxelWorld.Unload();
            CloseRouterPort();
            Cursor.lockState = CursorLockMode.None;
        }

        // Called at the end of every session, including after a failed connection. A host gets it
        // twice, for its server role and for its client role.
        private void OnSessionStopped(bool wasHost)
        {
            if (!sessionActive) return;

            bool wasServer = registry != null;
            sessionActive = false;
            EndSession();

            if (!leaving)
            {
                string reason = networkManager.DisconnectReason;
                if (!string.IsNullOrEmpty(worldSync.Failure)) message = worldSync.Failure;
                else if (ConnectionRefusal.IsKnown(reason)) message = reason;
                else message = wasServer ? "The server has stopped." : "Connection to the server lost or impossible.";

                if (wasServer) Debug.LogError(message);
            }

            leaving = false;
            if (wasServer) QuitIfDedicated();
        }

        // A dedicated server with no game left has no reason to keep running.
        private static void QuitIfDedicated()
        {
            if (Application.isBatchMode) Application.Quit(1);
        }

        private byte[] OwnConnectionRequest()
        {
            // Empty and not only null: after a script reload, the Unity Editor gives null strings
            // back as empty strings.
            if (string.IsNullOrEmpty(playerId)) playerId = PlayerIdentity.GetOrCreate();
            return new ConnectionRequest(playerId, password).ToBytes();
        }

        private void ReadSettings(string[] arguments)
        {
            if (ushort.TryParse(ArgumentValue(arguments, "-port"), out ushort requestedPort) && requestedPort > 0)
            {
                port = requestedPort;
            }

            bool validInterval = float.TryParse(
                ArgumentValue(arguments, "-autosave"), NumberStyles.Float, CultureInfo.InvariantCulture, out float interval);
            if (validInterval && interval > 0f)
            {
                autosaveInterval = interval;
            }

            password = ArgumentValue(arguments, "-password") ?? "";
            dataDirectory = ArgumentValue(arguments, "-data") ?? Application.persistentDataPath;

            string requestedId = ArgumentValue(arguments, "-playerid");
            if (ConnectionRequest.IsValidPlayerId(requestedId)) playerId = requestedId;

            bool validScale = float.TryParse(
                ArgumentValue(arguments, "-timescale"), NumberStyles.Float, CultureInfo.InvariantCulture, out float scale);
            if (validScale && scale >= 0f)
            {
                startTimeScale = scale;
            }

            if (Array.IndexOf(arguments, "-noupnp") >= 0) openRouterPort = false;

            string admins = ArgumentValue(arguments, "-admin");
            if (admins != null) adminIds = admins.Split(',');

            string[] size = (ArgumentValue(arguments, "-worldsize") ?? "").Split(',');
            if (size.Length == 3
                && int.TryParse(size[0], out int sizeX) && int.TryParse(size[1], out int sizeY) && int.TryParse(size[2], out int sizeZ)
                && sizeX > 0 && sizeY > 0 && sizeZ > 0)
            {
                worldSize = new Vector3Int(sizeX, sizeY, sizeZ);
            }
        }

        private string ScreenText()
        {
            if (!networkManager.IsListening)
            {
                return "F1: host a game\n"
                    + "Enter: join a game\n\n"
                    + $"Address: {address}{(editingPassword ? "" : "_")}\n"
                    + $"Password: {new string('*', password.Length)}{(editingPassword ? "_" : "")}\n"
                    + "Tab: switch field   Backspace: delete\n\n"
                    + message;
            }

            if (!networkManager.IsServer && !networkManager.IsConnectedClient)
            {
                return $"Connecting to {address}…\nF10: cancel";
            }

            if (worldSync.World == null)
            {
                return $"Loading the world… {Mathf.RoundToInt(worldSync.LoadingProgress * 100f)}%\nF10: cancel";
            }

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                string role = networkManager.IsHost ? $"Game hosted on port {port}" : $"Connected to {address}";
                return ecosystemSync.ClockText + "\n" + role + RouterPortText()
                    + "\nClick: take control\nF10: leave the game"
                    + "\nAdmins: F5 / F6 slow down or speed up time, F7 advance one day";
            }

            return ecosystemSync.ClockText;
        }

        // What the host needs to know to invite players from outside its network. Empty on a client.
        private string RouterPortText()
        {
            if (forwardingRequest != null) return "\nInternet: asking the router to open the port…";

            return forwarding != null ? "\n" + forwarding.Summary : "";
        }

        private void ListenToTextInput(Keyboard keyboard)
        {
            if (keyboard == textInputKeyboard) return;

            if (textInputKeyboard != null) textInputKeyboard.onTextInput -= OnTextInput;
            textInputKeyboard = keyboard;
            if (textInputKeyboard != null) textInputKeyboard.onTextInput += OnTextInput;
        }

        private void OnTextInput(char character)
        {
            if (networkManager.IsListening) return;

            if (editingPassword)
            {
                bool printable = character > ' ' && character < 127;
                if (printable && password.Length < MaxPasswordLength) password += character;
            }
            else if ((char.IsDigit(character) || character == '.') && address.Length < MaxAddressLength)
            {
                address += character;
            }
        }

        private static string WithoutLastCharacter(string text) =>
            text.Length > 0 ? text.Substring(0, text.Length - 1) : text;

        private static string ArgumentValue(string[] arguments, string name)
        {
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
