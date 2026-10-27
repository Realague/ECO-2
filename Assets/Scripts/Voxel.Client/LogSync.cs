using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The logs felled trees leave on the ground, one wood each (MiningRules.LogsOf). The server keeps
    // them (LyingLogs) and saves them with the world (logs.dat), drops a tree's logs along where it
    // fell (LogPlacement.AlongFall, shown once it has hit the ground), keeps them resting on the ground
    // when blocks change under or into them, and gives one to a player picking it up with a right
    // click if they can carry it. Every machine with a display draws them (LogViews).
    public class LogSync : MonoBehaviour
    {
        private const string AllMessage = "Log.All";
        private const string AddedMessage = "Log.Added";
        private const string RemovedMessage = "Log.Removed";
        private const string MovedMessage = "Log.Moved";
        private const string PickupMessage = "Log.Pickup";
        private const string SaveFileName = "logs.dat";

        private const int EntrySize = sizeof(int) + 4 * sizeof(float) + sizeof(ushort);

        // How close a pointer ray must pass to a log to pick it up, around its middle.
        private const float PickHalfWidth = 0.4f;
        private const float PickHeight = 0.35f;

        // A log is moved when a block changes this close above or below it in its column.
        private const int SettleRange = 2;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private Material cutMaterial;

        private struct Shown
        {
            public LyingLog Log;

            // A falling tree's logs show up once it has hit the ground.
            public float VisibleAt;
        }

        // Every machine: the logs lying in the world.
        private readonly Dictionary<int, Shown> shown = new Dictionary<int, Shown>();

        // Server side.
        private readonly List<(System.Numerics.Vector3 position, float yaw)> placed = new List<(System.Numerics.Vector3, float)>();
        private readonly List<LyingLog> added = new List<LyingLog>();
        private readonly List<int> inColumn = new List<int>();
        private LyingLogs store;
        private World serverWorld;
        private string savePath;

        private LogViews views;
        private bool running;

        private bool IsServer => store != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started, with the world's folder.
        public void StartServer(string directory)
        {
            running = true;
            savePath = Path.Combine(directory, SaveFileName);
            store = Load(savePath, ecosystemSync.PlantAssets.Count);
            foreach (LyingLog log in store.All)
            {
                shown[log.Id] = new Shown { Log = log };
            }

            serverWorld = worldSync.World;
            if (serverWorld != null) serverWorld.BlockChanged += OnBlockChanged;
            Messaging.RegisterNamedMessageHandler(PickupMessage, OnPickup);
            networkManager.OnClientConnectedCallback += OnClientConnected;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(AllMessage, OnAll);
            Messaging.RegisterNamedMessageHandler(AddedMessage, OnAdded);
            Messaging.RegisterNamedMessageHandler(RemovedMessage, OnRemoved);
            Messaging.RegisterNamedMessageHandler(MovedMessage, OnMoved);
        }

        public void Stop()
        {
            if (!running) return;

            if (IsServer)
            {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                if (serverWorld != null) serverWorld.BlockChanged -= OnBlockChanged;
            }

            store = null;
            serverWorld = null;
            shown.Clear();
            running = false;
        }

        // Server side: writes the logs lying in the world next to the other saves.
        public void Save()
        {
            if (store == null) return;

            try
            {
                store.SaveToFile(savePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the logs: {e.Message} ({savePath})");
            }
        }

        // Server side: a felled tree's logs, count of them from start to length along direction from
        // its foot, shown after delay seconds (once it has fallen).
        internal void DropAlongFall(Vector3 foot, Vector3 direction, float start, float length, int count, int species, float delay)
        {
            if (!IsServer || serverWorld == null || count <= 0) return;

            LogPlacement.AlongFall(serverWorld, foot.ToNumerics(), direction.ToNumerics(), start, length, count, placed);
            added.Clear();
            foreach ((System.Numerics.Vector3 position, float yaw) in placed)
            {
                added.Add(store.Add(position, yaw, species));
            }

            Publish(added, delay);
        }

        // Server side: one log where a stump was dug out.
        internal void DropAt(Vector3 foot, float yaw, int species)
        {
            if (!IsServer || serverWorld == null) return;

            int x = Mathf.FloorToInt(foot.x);
            int z = Mathf.FloorToInt(foot.z);
            var position = new System.Numerics.Vector3(foot.x, serverWorld.GetTopHeight(x, z), foot.z);
            if (LogPlacement.Settle(serverWorld, position, out System.Numerics.Vector3 settled)) position = settled;

            added.Clear();
            added.Add(store.Add(position, yaw, species));
            Publish(added, 0f);
        }

        // Server side: whether a log lies in this cell (storage cannot be placed over it).
        internal bool HasLogIn(BlockPos cell)
        {
            if (!IsServer) return false;

            store.InColumn(cell.X, cell.Z, inColumn);
            foreach (int id in inColumn)
            {
                if (store.TryGet(id, out LyingLog log) && log.Position.Y >= cell.Y - 0.5f && log.Position.Y < cell.Y + 1f) return true;
            }

            return false;
        }

        // The log the local player points at, at most maxDistance away (distance along the ray).
        public bool TryAimed(Vector3 origin, Vector3 direction, float maxDistance, out int logId, out float distance)
        {
            logId = 0;
            distance = maxDistance;
            float now = Time.time;
            System.Numerics.Vector3 from = origin.ToNumerics();
            System.Numerics.Vector3 to = (origin + direction * maxDistance).ToNumerics();
            foreach (Shown entry in shown.Values)
            {
                if (entry.VisibleAt > now) continue;

                System.Numerics.Vector3 position = entry.Log.Position;
                var min = new System.Numerics.Vector3(position.X - PickHalfWidth, position.Y, position.Z - PickHalfWidth);
                var max = new System.Numerics.Vector3(position.X + PickHalfWidth, position.Y + PickHeight, position.Z + PickHalfWidth);
                float along = ArrowFlight.SegmentEntry(from, to, min, max);
                if (along >= 0f && along < distance)
                {
                    distance = along;
                    logId = entry.Log.Id;
                }
            }

            return logId != 0;
        }

        // The local player picks up a log: the server decides, and says when it is gone.
        public void RequestPickup(int logId)
        {
            if (!running) return;

            if (IsServer)
            {
                Pickup(networkManager.LocalClientId, logId);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(logId);
                Messaging.SendNamedMessage(PickupMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void LateUpdate()
        {
            if (!running || Application.isBatchMode || shown.Count == 0) return;

            if (views == null) views = new LogViews(ecosystemSync.PlantAssets, cutMaterial);

            float now = Time.time;
            foreach (Shown entry in shown.Values)
            {
                if (entry.VisibleAt <= now) views.Add(entry.Log.Position.ToUnity(), entry.Log.Yaw, entry.Log.Species);
            }

            views.Render();
        }

        // Server side: logs a block changed under or into are moved to rest on what is there now.
        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            store.InColumn(pos.X, pos.Z, inColumn);
            foreach (int id in inColumn)
            {
                if (!store.TryGet(id, out LyingLog log) || Mathf.Abs(log.Position.Y - pos.Y) > SettleRange + 1) continue;
                if (!LogPlacement.Settle(serverWorld, log.Position, out System.Numerics.Vector3 settled)) continue;

                store.Move(id, settled);
                shown[id] = new Shown { Log = log.MovedTo(settled) };
                foreach (ulong clientId in networkManager.ConnectedClientsIds)
                {
                    if (clientId == networkManager.LocalClientId) continue;

                    using (var writer = new FastBufferWriter(sizeof(int) + 3 * sizeof(float), Allocator.Temp))
                    {
                        writer.WriteValueSafe(id);
                        writer.WriteValueSafe(settled.X);
                        writer.WriteValueSafe(settled.Y);
                        writer.WriteValueSafe(settled.Z);
                        Messaging.SendNamedMessage(MovedMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                    }
                }
            }
        }

        // Server side: new logs, shown here and sent to the other players.
        private void Publish(List<LyingLog> logs, float delay)
        {
            float visibleAt = Time.time + delay;
            foreach (LyingLog log in logs)
            {
                shown[log.Id] = new Shown { Log = log, VisibleAt = visibleAt };
            }

            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(sizeof(float) + sizeof(int) + logs.Count * EntrySize, Allocator.Temp))
                {
                    writer.WriteValueSafe(delay);
                    writer.WriteValueSafe(logs.Count);
                    foreach (LyingLog log in logs)
                    {
                        WriteLog(writer, log);
                    }

                    Messaging.SendNamedMessage(AddedMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
        }

        // Server side: a player picks up a log within reach, if they can carry its wood.
        private void Pickup(ulong clientId, int logId)
        {
            if (!store.TryGet(logId, out LyingLog log)) return;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + PickHalfWidth;
            if ((log.Position.ToUnity() - eyes).sqrMagnitude > reach * reach) return;
            if (!claimSync.Check(clientId, Mathf.FloorToInt(log.Position.X), Mathf.FloorToInt(log.Position.Z))) return;

            // A log is worth several wood, all taken at once or none.
            int wood = Items.WoodOf(log.Species);
            if (inventorySync.RoomFor(clientId, wood) < MiningRules.WoodPerLog)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(wood));
                return;
            }

            inventorySync.Give(clientId, wood, MiningRules.WoodPerLog);
            store.Remove(logId);
            shown.Remove(logId);
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
                {
                    writer.WriteValueSafe(logId);
                    Messaging.SendNamedMessage(RemovedMessage, other, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
        }

        // Server side: a joining player is sent every log lying in the world.
        private void OnClientConnected(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;

            using (var writer = new FastBufferWriter(sizeof(int) + store.Count * EntrySize, Allocator.Temp))
            {
                writer.WriteValueSafe(store.Count);
                foreach (LyingLog log in store.All)
                {
                    WriteLog(writer, log);
                }

                Messaging.SendNamedMessage(AllMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnPickup(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int logId);
            Pickup(senderId, logId);
        }

        private void OnAll(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            shown.Clear();
            for (int i = 0; i < count; i++)
            {
                if (TryReadLog(reader, out LyingLog log)) shown[log.Id] = new Shown { Log = log };
            }
        }

        private void OnAdded(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out float delay);
            reader.ReadValueSafe(out int count);
            float visibleAt = Time.time + (float.IsFinite(delay) ? Mathf.Clamp(delay, 0f, 5f) : 0f);
            for (int i = 0; i < count; i++)
            {
                if (TryReadLog(reader, out LyingLog log)) shown[log.Id] = new Shown { Log = log, VisibleAt = visibleAt };
            }
        }

        private void OnRemoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int logId);
            shown.Remove(logId);
        }

        private void OnMoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int logId);
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            if (!shown.TryGetValue(logId, out Shown entry) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return;

            entry.Log = entry.Log.MovedTo(new System.Numerics.Vector3(x, y, z));
            shown[logId] = entry;
        }

        // Logs of a species this game does not know (from a save made with more species) are dropped.
        private static LyingLogs Load(string path, int speciesCount)
        {
            if (!File.Exists(path)) return new LyingLogs();

            try
            {
                LyingLogs loaded = LyingLogs.LoadFromFile(path);
                var unknown = new List<int>();
                foreach (LyingLog log in loaded.All)
                {
                    if (log.Species >= speciesCount) unknown.Add(log.Id);
                }

                foreach (int id in unknown)
                {
                    loaded.Remove(id);
                }

                if (unknown.Count > 0) Debug.LogWarning($"{unknown.Count} logs of unknown plant species were dropped.");
                return loaded;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"The logs could not be loaded ({e.Message}): the world starts without any.");
                return new LyingLogs();
            }
        }

        private static void WriteLog(FastBufferWriter writer, LyingLog log)
        {
            writer.WriteValueSafe(log.Id);
            writer.WriteValueSafe(log.Position.X);
            writer.WriteValueSafe(log.Position.Y);
            writer.WriteValueSafe(log.Position.Z);
            writer.WriteValueSafe(log.Yaw);
            writer.WriteValueSafe((ushort)log.Species);
        }

        private bool TryReadLog(FastBufferReader reader, out LyingLog log)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            reader.ReadValueSafe(out float yaw);
            reader.ReadValueSafe(out ushort species);
            log = new LyingLog(id, new System.Numerics.Vector3(x, y, z), yaw, species);
            return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(yaw) && species < ecosystemSync.PlantAssets.Count;
        }
    }
}
