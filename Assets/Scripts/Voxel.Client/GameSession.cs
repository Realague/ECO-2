using System;
using System.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Démarre et arrête une partie : héberger (serveur et joueur local) ou rejoindre un hôte.
    // L'argument de ligne de commande « -connect <adresse> » rejoint directement un hôte au lancement.
    public class GameSession : MonoBehaviour
    {
        private const string ConnectArgument = "-connect";
        private const string LocalAddress = "127.0.0.1";
        private const string AnyAddress = "0.0.0.0";
        private const int MaxAddressLength = 15;
        private const int SpawnSlots = 8;
        private const int SpawnSpacing = 2;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private VoxelWorld voxelWorld;
        [SerializeField] private ushort port = 7777;

        private string address = LocalAddress;
        private string message = "";
        private bool leaving;
        private Keyboard textInputKeyboard;
        private GUIStyle textStyle;

        private UnityTransport Transport => (UnityTransport)networkManager.NetworkConfig.NetworkTransport;

        private void Start()
        {
            networkManager.ConnectionApprovalCallback = ApproveConnection;
            networkManager.OnClientStopped += OnClientStopped;

            string requestedAddress = CommandLineValue(ConnectArgument);
            if (requestedAddress != null)
            {
                address = requestedAddress;
                StartClient();
            }
        }

        private void OnDestroy()
        {
            ListenToTextInput(null);
            if (networkManager != null) networkManager.OnClientStopped -= OnClientStopped;
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            ListenToTextInput(keyboard);
            if (keyboard == null || networkManager.ShutdownInProgress) return;

            if (networkManager.IsListening)
            {
                if (keyboard.f10Key.wasPressedThisFrame)
                {
                    leaving = true;
                    networkManager.Shutdown();
                }

                return;
            }

            if (keyboard.backspaceKey.wasPressedThisFrame && address.Length > 0)
            {
                address = address.Substring(0, address.Length - 1);
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
            message = "";
            leaving = false;
            voxelWorld.Load(true);

            // L'hôte écoute sur toutes les interfaces pour être joignable depuis d'autres machines.
            Transport.SetConnectionData(LocalAddress, port, AnyAddress);
            if (!networkManager.StartHost())
            {
                voxelWorld.Unload();
                message = $"Impossible d'héberger une partie sur le port {port}.";
            }
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

            // Provisoire : tant que le serveur n'envoie pas les chunks, le client génère le même
            // monde que lui à partir de la graine.
            voxelWorld.Load(false);

            Transport.SetConnectionData(address, port);
            if (!networkManager.StartClient())
            {
                voxelWorld.Unload();
                message = $"Impossible de lancer la connexion à {address}.";
            }
        }

        // Exécuté par le serveur pour chaque joueur, hôte compris : il décide où le joueur apparaît.
        private void ApproveConnection(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            response.Approved = true;
            response.CreatePlayerObject = true;
            response.Position = SpawnPosition(request.ClientNetworkId);
            response.Rotation = Quaternion.identity;
        }

        private Vector3 SpawnPosition(ulong clientId)
        {
            World world = voxelWorld.World;
            int x = world.SizeX / 2 + (int)(clientId % SpawnSlots) * SpawnSpacing;
            int z = world.SizeZ / 2;
            return new Vector3(x + 0.5f, world.GetSurfaceHeight(x, z), z + 0.5f);
        }

        // Appelé à la fin de toute partie, côté hôte comme côté client, y compris après un échec de connexion.
        private void OnClientStopped(bool wasHost)
        {
            voxelWorld.Unload();
            Cursor.lockState = CursorLockMode.None;

            if (!leaving)
            {
                message = wasHost ? "La partie hébergée s'est arrêtée." : "Connexion au serveur perdue ou impossible.";
            }

            leaving = false;
        }

        private string ScreenText()
        {
            if (!networkManager.IsListening)
            {
                return "F1 : héberger une partie\n"
                    + $"Entrée : rejoindre {address}_\n"
                    + "Chiffres, point et Retour arrière : modifier l'adresse\n\n"
                    + message;
            }

            if (!networkManager.IsServer && !networkManager.IsConnectedClient)
            {
                return $"Connexion à {address}…\nF10 : annuler";
            }

            if (Cursor.lockState != CursorLockMode.Locked)
            {
                string role = networkManager.IsHost ? $"Partie hébergée sur le port {port}" : $"Connecté à {address}";
                return role + "\nClic : prendre le contrôle\nF10 : quitter la partie";
            }

            return "";
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
            if (networkManager.IsListening || address.Length >= MaxAddressLength) return;

            if (char.IsDigit(character) || character == '.')
            {
                address += character;
            }
        }

        private static string CommandLineValue(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, name);
            return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
        }
    }
}
