using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Côté serveur : admet les joueurs (mot de passe, identifiant unique) et retient où chacun se
    // trouvait, pour le faire réapparaître au même endroit à sa prochaine connexion.
    internal sealed class PlayerRegistry
    {
        private const string FileName = "players.dat";
        private const int SpawnSlots = 8;
        private const int SpawnSpacing = 2;

        private readonly NetworkManager networkManager;
        private readonly World world;
        private readonly string password;
        private readonly string filePath;
        private readonly PlayerStore store;
        private readonly Dictionary<ulong, string> connectedPlayers = new Dictionary<ulong, string>();

        // Un mot de passe vide laisse entrer tout le monde.
        public PlayerRegistry(NetworkManager networkManager, World world, string directory, string password)
        {
            this.networkManager = networkManager;
            this.world = world;
            this.password = password;
            filePath = Path.Combine(directory, FileName);
            store = LoadStore();

            networkManager.ConnectionApprovalCallback = Approve;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        public int ConnectedCount => connectedPlayers.Count;

        public void Close()
        {
            networkManager.ConnectionApprovalCallback = null;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        // Relève la position de chaque joueur connecté. À appeler à chaque image : quand le serveur
        // apprend une déconnexion, l'objet du joueur n'est déjà plus lisible, il faut donc l'avoir relevée avant.
        public void TrackPlayers()
        {
            foreach (KeyValuePair<ulong, string> player in connectedPlayers)
            {
                if (!networkManager.ConnectedClients.TryGetValue(player.Key, out NetworkClient client)) continue;
                if (client.PlayerObject == null) continue;

                Transform transform = client.PlayerObject.transform;
                store.Set(player.Value, new PlayerState
                {
                    Position = transform.position.ToNumerics(),
                    Yaw = transform.eulerAngles.y
                });
            }
        }

        public void Save()
        {
            TrackPlayers();

            try
            {
                store.SaveToFile(filePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Échec de la sauvegarde des joueurs : {e.Message} ({filePath})");
            }
        }

        private PlayerStore LoadStore()
        {
            if (File.Exists(filePath))
            {
                try
                {
                    return PlayerStore.LoadFromFile(filePath);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Sauvegarde des joueurs illisible, elle sera remplacée à la prochaine sauvegarde : {e.Message} ({filePath})");
                }
            }

            return new PlayerStore();
        }

        private void Approve(
            NetworkManager.ConnectionApprovalRequest request,
            NetworkManager.ConnectionApprovalResponse response)
        {
            string refusal = RefusalFor(request.Payload, out ConnectionRequest connection);
            if (refusal != null)
            {
                response.Approved = false;
                response.Reason = refusal;
                Debug.Log($"Connexion refusée : {refusal}");
                return;
            }

            connectedPlayers[request.ClientNetworkId] = connection.PlayerId;

            PlayerState state = StartingState(connection.PlayerId, request.ClientNetworkId);
            response.Approved = true;
            response.CreatePlayerObject = true;
            response.Position = state.Position.ToUnity();
            response.Rotation = Quaternion.Euler(0f, state.Yaw, 0f);

            Debug.Log($"Joueur {connection.PlayerId} connecté ({connectedPlayers.Count} en ligne).");
        }

        // Renvoie le motif du refus, ou null si la connexion est acceptée.
        private string RefusalFor(byte[] payload, out ConnectionRequest connection)
        {
            if (!ConnectionRequest.TryParse(payload, out connection)) return ConnectionRefusal.InvalidRequest;
            if (password.Length > 0 && connection.Password != password) return ConnectionRefusal.WrongPassword;
            if (connectedPlayers.ContainsValue(connection.PlayerId)) return ConnectionRefusal.AlreadyConnected;
            return null;
        }

        private PlayerState StartingState(string playerId, ulong clientId)
        {
            if (store.TryGet(playerId, out PlayerState saved))
            {
                System.Numerics.Vector3 p = saved.Position;
                if (world.InBounds((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z)))
                {
                    // Le monde a pu changer depuis : on ne fait jamais réapparaître un joueur dans un bloc.
                    saved.Position = VoxelCollision.RaiseToFreeSpace(world, p, PlayerRules.HalfWidth, PlayerRules.Height);
                    return saved;
                }
            }

            int x = world.SizeX / 2 + (int)(clientId % SpawnSlots) * SpawnSpacing;
            int z = world.SizeZ / 2;
            return new PlayerState
            {
                Position = new System.Numerics.Vector3(x + 0.5f, world.GetSurfaceHeight(x, z), z + 0.5f)
            };
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (!connectedPlayers.TryGetValue(clientId, out string playerId)) return;

            // Sa dernière position a été relevée par TrackPlayers : il reste à l'écrire.
            connectedPlayers.Remove(clientId);
            Save();

            Debug.Log($"Joueur {playerId} déconnecté ({connectedPlayers.Count} en ligne).");
        }
    }
}
