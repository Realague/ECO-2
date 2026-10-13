using System;
using System.Globalization;
using System.IO;
using System.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Démarre et arrête une partie : héberger (serveur et joueur local), rejoindre un serveur, ou
    // tourner en serveur dédié, sans joueur local ni affichage.
    //
    // Arguments de ligne de commande :
    //   -server               serveur dédié (implicite avec -batchmode)
    //   -host                 héberge une partie dès le lancement
    //   -connect <adresse>    rejoint un serveur dès le lancement
    //   -port <port>          port UDP, 7777 par défaut
    //   -password <mot>       mot de passe exigé par le serveur, ou présenté par le client
    //   -data <dossier>       dossier des sauvegardes du serveur
    //   -autosave <secondes>  intervalle de sauvegarde automatique du serveur
    //   -playerid <id>        identifiant de joueur, à la place de celui enregistré sur la machine
    //   -timescale <facteur>  vitesse de la simulation au démarrage du serveur, 1 par défaut
    //   -admin <id,id,...>    identifiants des joueurs autorisés à régler la vitesse de la simulation
    public class GameSession : MonoBehaviour
    {
        private const string LocalAddress = "127.0.0.1";
        private const string AnyAddress = "0.0.0.0";
        private const int MaxAddressLength = 15;
        private const int MaxPasswordLength = 32;

        // Vitesses de simulation parcourues par les touches F5 et F6. Zéro met le temps en pause.
        private static readonly float[] TimeScales = { 0f, 1f, 10f, 100f, 1000f, 10000f };

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private VoxelWorld voxelWorld;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private ushort port = 7777;
        [SerializeField] private float autosaveInterval = 60f;

        private string address = LocalAddress;
        private string password = "";
        private string playerId;
        private string dataDirectory;
        private float startTimeScale = 1f;
        private string[] adminIds = Array.Empty<string>();
        private string message = "";
        private bool editingPassword;
        private bool sessionActive;
        private bool leaving;
        private PlayerRegistry registry;
        private float nextAutosave;
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
            SaveSession();
        }

        private void Update()
        {
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
                    // Les joueurs disparaissent pendant l'arrêt : on relève leur position avant.
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

            // Texte doublé d'une ombre pour rester lisible sur le ciel comme sur le terrain.
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
                message = $"« {address} » n'est pas une adresse IPv4 valide.";
                return;
            }

            networkManager.NetworkConfig.ConnectionData = OwnConnectionRequest();
            Transport.SetConnectionData(address, port);
            if (!networkManager.StartClient())
            {
                message = $"Impossible de lancer la connexion à {address}.";
                return;
            }

            // Le monde du client est créé par WorldSync à partir de ce que le serveur envoie.
            worldSync.StartClient();
            ecosystemSync.StartClient();
            sessionActive = true;
        }

        private void StartServerSession(bool withLocalPlayer)
        {
            message = "";
            leaving = false;

            Directory.CreateDirectory(dataDirectory);
            voxelWorld.LoadPersistent(dataDirectory);
            registry = new PlayerRegistry(networkManager, voxelWorld.World, dataDirectory, password);

            // Le serveur écoute sur toutes les interfaces pour être joignable depuis d'autres machines.
            Transport.SetConnectionData(LocalAddress, port, AnyAddress);

            bool started;
            if (withLocalPlayer)
            {
                networkManager.NetworkConfig.ConnectionData = OwnConnectionRequest();
                started = networkManager.StartHost();
            }
            else
            {
                // Sans affichage, rien ne limite la cadence : on la cale sur celle du réseau.
                Application.targetFrameRate = (int)networkManager.NetworkConfig.TickRate;
                started = networkManager.StartServer();
            }

            if (!started)
            {
                EndSession();
                message = $"Impossible de démarrer un serveur sur le port {port}.";
                Debug.LogError(message);
                QuitIfDedicated();
                return;
            }

            worldSync.StartServer();
            ecosystemSync.StartServer(voxelWorld.World, dataDirectory, startTimeScale, IsAdmin);
            sessionActive = true;
            nextAutosave = Time.unscaledTime + autosaveInterval;
            Debug.Log($"Serveur démarré sur le port {port}. Sauvegardes dans {dataDirectory}, toutes les {autosaveInterval} s.");
        }

        // Écrit le monde, l'écosystème et les joueurs sur le disque. Sans effet hors du rôle de serveur.
        private void SaveSession()
        {
            if (registry == null) return;

            registry.Save();
            voxelWorld.Save();
            ecosystemSync.Save();
        }

        // L'hôte est administrateur de sa propre partie ; sur un serveur dédié, ce sont les joueurs
        // désignés par l'argument -admin.
        private bool IsAdmin(ulong clientId)
        {
            if (networkManager.IsHost && clientId == networkManager.LocalClientId) return true;

            return registry != null
                && registry.TryGetPlayerId(clientId, out string id)
                && Array.IndexOf(adminIds, id) >= 0;
        }

        // Vitesse suivante ou précédente dans la liste, à partir de la vitesse actuelle.
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
            ecosystemSync.Stop();
            worldSync.Stop();
            if (registry != null)
            {
                registry.Close();
                registry = null;
            }

            voxelWorld.Unload();
            Cursor.lockState = CursorLockMode.None;
        }

        // Appelé à la fin de toute partie, y compris après un échec de connexion. Un hôte le reçoit
        // deux fois, pour son rôle de serveur et pour son rôle de client.
        private void OnSessionStopped(bool wasHost)
        {
            if (!sessionActive) return;

            bool wasServer = registry != null;
            sessionActive = false;
            EndSession();

            if (!leaving)
            {
                string reason = networkManager.DisconnectReason;
                if (ConnectionRefusal.IsKnown(reason)) message = reason;
                else message = wasServer ? "Le serveur s'est arrêté." : "Connexion au serveur perdue ou impossible.";

                if (wasServer) Debug.LogError(message);
            }

            leaving = false;
            if (wasServer) QuitIfDedicated();
        }

        // Un serveur dédié qui n'a plus de partie n'a plus de raison de tourner.
        private static void QuitIfDedicated()
        {
            if (Application.isBatchMode) Application.Quit(1);
        }

        private byte[] OwnConnectionRequest()
        {
            if (playerId == null) playerId = PlayerIdentity.GetOrCreate();
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

            string admins = ArgumentValue(arguments, "-admin");
            if (admins != null) adminIds = admins.Split(',');
        }

        private string ScreenText()
        {
            if (!networkManager.IsListening)
            {
                return "F1 : héberger une partie\n"
                    + "Entrée : rejoindre une partie\n\n"
                    + $"Adresse : {address}{(editingPassword ? "" : "_")}\n"
                    + $"Mot de passe : {new string('*', password.Length)}{(editingPassword ? "_" : "")}\n"
                    + "Tab : changer de champ   Retour arrière : effacer\n\n"
                    + message;
            }

            if (!networkManager.IsServer && !networkManager.IsConnectedClient)
            {
                return $"Connexion à {address}…\nF10 : annuler";
            }

            if (worldSync.World == null)
            {
                return $"Chargement du monde… {Mathf.RoundToInt(worldSync.LoadingProgress * 100f)} %\nF10 : annuler";
            }

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                string role = networkManager.IsHost ? $"Partie hébergée sur le port {port}" : $"Connecté à {address}";
                return ecosystemSync.ClockText + "\n" + role
                    + "\nClic : prendre le contrôle\nF10 : quitter la partie"
                    + "\nAdministrateurs : F5 / F6 ralentir ou accélérer le temps, F7 avancer d'un jour";
            }

            return ecosystemSync.ClockText;
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
