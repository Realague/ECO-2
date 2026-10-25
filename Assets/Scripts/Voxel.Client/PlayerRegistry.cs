using System;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Server side: admits players (password, unique id) and remembers where each one was,
    // to respawn them at the same place at their next connection.
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

        // An empty password lets everyone in.
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

        public bool TryGetPlayerId(ulong clientId, out string playerId) => connectedPlayers.TryGetValue(clientId, out playerId);

        // The saved state of a connected player: position, speciality and inventory.
        public bool TryGetState(ulong clientId, out PlayerState state)
        {
            state = null;
            if (!connectedPlayers.TryGetValue(clientId, out string playerId)) return false;

            state = store.GetOrAdd(playerId);
            return true;
        }

        // The connected players: NGO client id and player id.
        public IEnumerable<KeyValuePair<ulong, string>> Connected => connectedPlayers;

        // The name a known player chose, connected or not (empty if none yet).
        public string NameOf(string playerId) => store.TryGet(playerId, out PlayerState state) ? state.Name : "";

        public bool IsNameTaken(string name, string playerId) => store.IsNameTaken(name, playerId);

        // The player id of a known player by name (names are unique, ignoring case).
        public bool TryFindByName(string name, out string playerId)
        {
            playerId = null;
            foreach (KeyValuePair<ulong, string> player in connectedPlayers)
            {
                if (PlayerNames.SameName(NameOf(player.Value), name))
                {
                    playerId = player.Value;
                    return true;
                }
            }

            return store.TryFindByName(name, out playerId);
        }

        public bool IsConnected(string playerId) => connectedPlayers.ContainsValue(playerId);

        // The access list an owner manages (storage, land): the named players connected, then those
        // allowed who are not, by name.
        public List<AccessEntry> AccessEntries(string ownerId, ICollection<string> allowed)
        {
            var entries = new List<AccessEntry>();
            foreach (KeyValuePair<ulong, string> player in connectedPlayers)
            {
                string name = NameOf(player.Value);
                if (player.Value == ownerId || name.Length == 0) continue;

                entries.Add(new AccessEntry(name, true, allowed.Contains(player.Value)));
            }

            foreach (string allowedId in allowed)
            {
                if (IsConnected(allowedId)) continue;

                string name = NameOf(allowedId);
                if (name.Length > 0) entries.Add(new AccessEntry(name, false, true));
            }

            entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        public void Close()
        {
            networkManager.ConnectionApprovalCallback = null;
            networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        // Records the position of every connected player. To call every frame: when the server learns
        // of a disconnection, the player's object is already unreadable, so it must have been recorded before.
        public void TrackPlayers()
        {
            foreach (KeyValuePair<ulong, string> player in connectedPlayers)
            {
                if (!networkManager.ConnectedClients.TryGetValue(player.Key, out NetworkClient client)) continue;
                if (client.PlayerObject == null) continue;

                Transform transform = client.PlayerObject.transform;
                PlayerState state = store.GetOrAdd(player.Value);
                state.Position = transform.position.ToNumerics();
                state.Yaw = transform.eulerAngles.y;
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
                Debug.LogError($"Failed to save the players: {e.Message} ({filePath})");
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
                        $"Unreadable player save, it will be replaced at the next save: {e.Message} ({filePath})");
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
                Debug.Log($"Connection refused: {refusal}");
                return;
            }

            connectedPlayers[request.ClientNetworkId] = connection.PlayerId;

            PlayerState state = StartingState(connection.PlayerId, request.ClientNetworkId);
            response.Approved = true;
            response.CreatePlayerObject = true;
            response.Position = state.Position.ToUnity();
            response.Rotation = Quaternion.Euler(0f, state.Yaw, 0f);

            Debug.Log($"Player {connection.PlayerId} connected ({connectedPlayers.Count} online).");
        }

        // Returns the reason for the refusal, or null if the connection is accepted.
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
                    // The world may have changed since: a player is never respawned inside a block.
                    saved.Position = VoxelCollision.RaiseToFreeSpace(world, p, PlayerRules.HalfWidth, PlayerRules.Height);
                    return saved;
                }
            }

            // New player, or saved position outside the world: they spawn at the centre, and their state
            // (inventory included) is kept from now on.
            int x = world.SizeX / 2 + (int)(clientId % SpawnSlots) * SpawnSpacing;
            int z = world.SizeZ / 2;
            PlayerState state = store.GetOrAdd(playerId);
            state.Position = new System.Numerics.Vector3(x + 0.5f, world.GetSurfaceHeight(x, z), z + 0.5f);
            state.Yaw = 0f;
            return state;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (!connectedPlayers.TryGetValue(clientId, out string playerId)) return;

            // Their last position was recorded by TrackPlayers: it only remains to write it.
            connectedPlayers.Remove(clientId);
            Save();

            Debug.Log($"Player {playerId} disconnected ({connectedPlayers.Count} online).");
        }
    }
}
