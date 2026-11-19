using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The rubble a block broken with a pickaxe leaves (MiningRules.LeavesRubble), instead of going
    // into the breaker's bag: MiningRules.RubblePieces pieces (one for a block a player placed,
    // RubblePiles.MarkPlaced), each worth one of its rock, the first also holding its ore. The server
    // keeps them (RubblePiles) and saves them with the world (rubble.dat), scatters them about the
    // broken block's cell and lets them fall to the ground (RubbleScatter, under water too), keeps them
    // resting when blocks change under or into them, and gives a piece to a player picking it up with
    // a right click: its rock and the ore it holds, both or nothing (InventorySync.UnfittingDrop).
    // Every machine with a display draws them (RubbleViews), flying out of the broken block when new,
    // and the local player collides with them (ICollisionObstacles: they stop a walker and can be
    // stood on, and a step up climbs them).
    public class RubbleSync : MonoBehaviour, ICollisionObstacles
    {
        private const string AllMessage = "Rubble.All";
        private const string AddedMessage = "Rubble.Added";
        private const string RemovedMessage = "Rubble.Removed";
        private const string MovedMessage = "Rubble.Moved";
        private const string PickupMessage = "Rubble.Pickup";
        private const string SaveFileName = "rubble.dat";

        private const int EntrySize = sizeof(int) + 4 * sizeof(float) + sizeof(ushort);

        // Rubble is moved when a block changes this close above or below it in its column.
        private const int SettleRange = 2;

        // How new rubble flies out of the broken block (display only): from its middle to where it
        // rests, falling under this gravity, at least this long, then bouncing once.
        private const float Gravity = 20f;
        private const float LeastFlightSeconds = 0.25f;
        private const float BounceSeconds = 0.22f;
        private const float BounceHeight = 0.08f;

        // Rubble is drawn this close to the camera.
        private const float ViewDistance = 96f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private VoxelWorld voxelWorld;
        [SerializeField] private HungerSync hungerSync;

        private struct Shown
        {
            public Rubble Rubble;

            // New rubble flies from this point, starting then; Flying false for rubble already lying.
            public bool Flying;
            public Vector3 From;
            public float FellAt;
        }

        // Every machine: the rubble lying in the world, and its ids by column (for collisions).
        private readonly Dictionary<int, Shown> shown = new Dictionary<int, Shown>();
        private readonly Dictionary<long, List<int>> shownByColumn = new Dictionary<long, List<int>>();

        // Server side.
        private readonly List<int> inColumn = new List<int>();
        private readonly List<System.Numerics.Vector3> spots = new List<System.Numerics.Vector3>();
        private readonly List<Rubble> added = new List<Rubble>();
        private RubblePiles store;
        private World serverWorld;
        private string savePath;

        private RubbleViews views;
        private bool running;

        private bool IsServer => store != null;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started, with the world's folder.
        public void StartServer(string directory)
        {
            running = true;
            savePath = Path.Combine(directory, SaveFileName);
            store = Load(savePath);
            foreach (Rubble rubble in store.All) Show(Lying(rubble));

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
            shownByColumn.Clear();
            running = false;
        }

        private void OnDestroy()
        {
            views?.Destroy();
        }

        // Server side: writes the rubble lying in the world next to the other saves.
        public void Save()
        {
            if (store == null) return;

            try
            {
                store.SaveToFile(savePath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the rubble: {e.Message} ({savePath})");
            }
        }

        // Server side: a player placed a block that leaves rubble at pos (WorldSync).
        internal void MarkPlaced(BlockPos pos)
        {
            if (IsServer) store.MarkPlaced(pos);
        }

        // Server side: the rubble of a block just broken at pos, scattered about its cell and fallen to
        // the ground: MiningRules.RubblePieces pieces, or one if a player had placed it there.
        internal void Drop(BlockPos pos, ushort block)
        {
            if (!IsServer || serverWorld == null) return;

            int count = store.TakePlaced(pos) ? 1 : MiningRules.RubblePieces;
            RubbleScatter.Spots(serverWorld, pos, count, UnityEngine.Random.Range(0f, 2f * Mathf.PI), spots);
            added.Clear();
            for (int i = 0; i < spots.Count; i++)
            {
                // The first piece holds the ore, the others are the rock alone.
                ushort piece = i == 0 ? block : Blocks.TypeOf(block);
                added.Add(store.Add(spots[i], UnityEngine.Random.Range(0f, 2f * Mathf.PI), piece));
            }

            var from = new Vector3(pos.X + 0.5f, pos.Y + 0.4f, pos.Z + 0.5f);
            foreach (Rubble rubble in added) Show(Flying(rubble, from));

            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(3 * sizeof(float) + sizeof(int) + added.Count * EntrySize, Allocator.Temp))
                {
                    writer.WriteValueSafe(from.x);
                    writer.WriteValueSafe(from.y);
                    writer.WriteValueSafe(from.z);
                    writer.WriteValueSafe(added.Count);
                    foreach (Rubble rubble in added) Write(writer, rubble);

                    Messaging.SendNamedMessage(AddedMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
        }

        // Server side: whether rubble lies in this cell (storage cannot be placed over it).
        internal bool HasRubbleIn(BlockPos cell)
        {
            if (!IsServer) return false;

            store.InColumn(cell.X, cell.Z, inColumn);
            foreach (int id in inColumn)
            {
                if (store.TryGet(id, out Rubble rubble) && rubble.Position.Y >= cell.Y - 0.5f && rubble.Position.Y < cell.Y + 1f) return true;
            }

            return false;
        }

        // The rubble the local player points at, at most maxDistance away (distance along the ray).
        public bool TryAimed(Vector3 origin, Vector3 direction, float maxDistance, out int rubbleId, out float distance)
        {
            rubbleId = 0;
            distance = maxDistance;
            float now = Time.time;
            System.Numerics.Vector3 from = origin.ToNumerics();
            System.Numerics.Vector3 to = (origin + direction * maxDistance).ToNumerics();
            foreach (Shown entry in shown.Values)
            {
                if (IsFlying(entry, now)) continue;

                BoxOf(entry.Rubble, out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);
                float along = ArrowFlight.SegmentEntry(from, to, min, max);
                if (along >= 0f && along < distance)
                {
                    distance = along;
                    rubbleId = entry.Rubble.Id;
                }
            }

            return rubbleId != 0;
        }

        // The pieces lying about these columns stop the local player (VoxelCollision.Move); a piece
        // still flying out does not.
        public void Collect(int minX, int maxX, int minZ, int maxZ, List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)> into)
        {
            if (shown.Count == 0) return;

            float now = Time.time;

            // A piece's box may reach into the columns beside its own.
            for (int z = minZ - 1; z <= maxZ + 1; z++)
            {
                for (int x = minX - 1; x <= maxX + 1; x++)
                {
                    if (!shownByColumn.TryGetValue(LyingLogs.ColumnKey(x, z), out List<int> ids)) continue;

                    foreach (int id in ids)
                    {
                        if (!shown.TryGetValue(id, out Shown entry) || IsFlying(entry, now)) continue;

                        BoxOf(entry.Rubble, out System.Numerics.Vector3 min, out System.Numerics.Vector3 max);
                        if (max.X > minX && min.X < maxX + 1 && max.Z > minZ && min.Z < maxZ + 1) into.Add((min, max));
                    }
                }
            }
        }

        // The block rubble came from and where it lies, for the aiming label.
        public bool TryGetShown(int rubbleId, out ushort block, out int x, out int z)
        {
            bool found = shown.TryGetValue(rubbleId, out Shown entry);
            block = entry.Rubble.Block;
            x = Mathf.FloorToInt(entry.Rubble.Position.X);
            z = Mathf.FloorToInt(entry.Rubble.Position.Z);
            return found;
        }

        // The local player picks up rubble: the server decides, and says when it is gone.
        public void RequestPickup(int rubbleId)
        {
            if (!running) return;

            if (IsServer)
            {
                Pickup(networkManager.LocalClientId, rubbleId);
                return;
            }

            using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(rubbleId);
                Messaging.SendNamedMessage(PickupMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // A piece's mesh as drawn, for the pickup halo.
        internal void CollectRubbleParts(int rubbleId, List<(Mesh mesh, Matrix4x4 matrix)> into)
        {
            if (views == null || !shown.TryGetValue(rubbleId, out Shown entry)) return;

            Vector3 position = ShownPosition(entry, Time.time, TerrainSettings.Smooth ? worldSync.World : null);
            views.CollectParts(position, entry.Rubble.Yaw, entry.Rubble.Block, entry.Rubble.Id, into);
        }

        private void LateUpdate()
        {
            if (!running || Application.isBatchMode || shown.Count == 0 || voxelWorld == null || voxelWorld.BlockMaterial == null) return;

            if (views == null) views = new RubbleViews(voxelWorld.BlockMaterial);

            Camera camera = Camera.main;
            Vector3 eye = camera != null ? camera.transform.position : Vector3.zero;
            float now = Time.time;
            World world = TerrainSettings.Smooth ? worldSync.World : null;
            foreach (Shown entry in shown.Values)
            {
                Vector3 position = ShownPosition(entry, now, world);
                if (camera != null && (position - eye).sqrMagnitude > ViewDistance * ViewDistance) continue;

                views.Draw(position, entry.Rubble.Yaw, entry.Rubble.Block, entry.Rubble.Id);
            }
        }

        // The box a piece fills where it lies (by the blocks: it is drawn on smoothed ground too).
        private static void BoxOf(Rubble rubble, out System.Numerics.Vector3 min, out System.Numerics.Vector3 max)
        {
            System.Numerics.Vector3 p = rubble.Position;
            min = new System.Numerics.Vector3(p.X - RubbleViews.HalfWidth, p.Y, p.Z - RubbleViews.HalfWidth);
            max = new System.Numerics.Vector3(p.X + RubbleViews.HalfWidth, p.Y + RubbleViews.Height, p.Z + RubbleViews.HalfWidth);
        }

        private static Shown Lying(Rubble rubble) => new Shown { Rubble = rubble };

        private static Shown Flying(Rubble rubble, Vector3 from) => new Shown { Rubble = rubble, Flying = true, From = from, FellAt = Time.time };

        private void Show(Shown entry)
        {
            Hide(entry.Rubble.Id);
            shown[entry.Rubble.Id] = entry;
            long key = ColumnKeyOf(entry.Rubble);
            if (!shownByColumn.TryGetValue(key, out List<int> ids))
            {
                ids = new List<int>();
                shownByColumn.Add(key, ids);
            }

            ids.Add(entry.Rubble.Id);
        }

        private void Hide(int rubbleId)
        {
            if (!shown.TryGetValue(rubbleId, out Shown entry)) return;

            shown.Remove(rubbleId);
            long key = ColumnKeyOf(entry.Rubble);
            if (!shownByColumn.TryGetValue(key, out List<int> ids)) return;

            ids.Remove(rubbleId);
            if (ids.Count == 0) shownByColumn.Remove(key);
        }

        private static long ColumnKeyOf(Rubble rubble) => LyingLogs.ColumnKey(Mathf.FloorToInt(rubble.Position.X), Mathf.FloorToInt(rubble.Position.Z));

        private static float FlightSeconds(Shown entry)
        {
            float drop = Mathf.Max(0f, entry.From.y - entry.Rubble.Position.Y);
            return Mathf.Max(LeastFlightSeconds, Mathf.Sqrt(2f * drop / Gravity));
        }

        // Still on its way out (it cannot be picked up nor stops anyone then).
        private static bool IsFlying(Shown entry, float now) => entry.Flying && now - entry.FellAt < FlightSeconds(entry);

        // Where it is drawn now: flying from the middle of the broken block, across to where it rests
        // and down under gravity (a small hop first if it lands no lower), then a small bounce, then at
        // rest (on smoothed ground at the height it shows, LogSync.OnShownGround).
        private static Vector3 ShownPosition(Shown entry, float now, World world)
        {
            Vector3 rest = LogSync.OnShownGround(world, entry.Rubble.Position.ToUnity());
            if (!entry.Flying) return rest;

            float t = now - entry.FellAt;
            float flight = FlightSeconds(entry);
            if (t < flight)
            {
                // Under gravity from the start's height to the rest's, over the flight's time.
                float share = t / flight;
                float rise = (rest.y - entry.From.y + 0.5f * Gravity * flight * flight) / flight;
                float y = entry.From.y + rise * t - 0.5f * Gravity * t * t;
                return new Vector3(Mathf.Lerp(entry.From.x, rest.x, share), y, Mathf.Lerp(entry.From.z, rest.z, share));
            }

            float bounce = (t - flight) / BounceSeconds;
            if (bounce < 1f) rest.y += BounceHeight * Mathf.Sin(bounce * Mathf.PI);
            return rest;
        }

        // Server side: rubble a block changed under or into is moved to rest on what is there now.
        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            store.InColumn(pos.X, pos.Z, inColumn);
            foreach (int id in inColumn)
            {
                if (!store.TryGet(id, out Rubble rubble) || Mathf.Abs(rubble.Position.Y - pos.Y) > SettleRange + 1) continue;
                if (!LogPlacement.Settle(serverWorld, rubble.Position, out System.Numerics.Vector3 settled, false)) continue;

                store.Move(id, settled);
                Show(Lying(rubble.MovedTo(settled)));
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

        // Server side: a player picks up a piece within reach, if they can carry its rock and its ore,
        // for a calorie (HungerRules.PerRubblePickedUp; a starving player cannot, as for any effort).
        private void Pickup(ulong clientId, int rubbleId)
        {
            if (!store.TryGet(rubbleId, out Rubble rubble)) return;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + RubbleViews.HalfWidth;
            if ((rubble.Position.ToUnity() - eyes).sqrMagnitude > reach * reach) return;
            if (!claimSync.Check(clientId, Mathf.FloorToInt(rubble.Position.X), Mathf.FloorToInt(rubble.Position.Z))) return;
            if (hungerSync != null && hungerSync.IsStarving(clientId)) return;

            // The rock and the ore it holds, together: both must fit.
            int unfitting = inventorySync.UnfittingDrop(clientId, rubble.Block);
            if (unfitting != Items.None)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(unfitting));
                return;
            }

            inventorySync.Give(clientId, Items.DropOf(rubble.Block), 1);
            OreKind ore = Ores.OreOf(rubble.Block);
            if (ore != OreKind.None) inventorySync.Give(clientId, Items.Ore(ore), Ores.Yield(ore));
            hungerSync?.BurnEffort(clientId, HungerRules.PerRubblePickedUp);

            store.Remove(rubbleId);
            Hide(rubbleId);
            foreach (ulong other in networkManager.ConnectedClientsIds)
            {
                if (other == networkManager.LocalClientId) continue;

                using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
                {
                    writer.WriteValueSafe(rubbleId);
                    Messaging.SendNamedMessage(RemovedMessage, other, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
        }

        // Server side: a joining player is sent all the rubble lying in the world.
        private void OnClientConnected(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;

            using (var writer = new FastBufferWriter(sizeof(int) + store.Count * EntrySize, Allocator.Temp))
            {
                writer.WriteValueSafe(store.Count);
                foreach (Rubble rubble in store.All) Write(writer, rubble);

                Messaging.SendNamedMessage(AllMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void OnPickup(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int rubbleId);
            Pickup(senderId, rubbleId);
        }

        private void OnAll(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int count);
            shown.Clear();
            shownByColumn.Clear();
            for (int i = 0; i < count; i++)
            {
                if (TryRead(reader, out Rubble rubble)) Show(Lying(rubble));
            }
        }

        private void OnAdded(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            reader.ReadValueSafe(out int count);
            var from = new Vector3(x, y, z);
            bool fromValid = float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
            for (int i = 0; i < count; i++)
            {
                if (!TryRead(reader, out Rubble rubble)) continue;

                // Only from near where it lies (a made-up start would send it flying across the world).
                bool near = fromValid && (from - rubble.Position.ToUnity()).sqrMagnitude < 64f;
                Show(near ? Flying(rubble, from) : Lying(rubble));
            }
        }

        private void OnRemoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int rubbleId);
            Hide(rubbleId);
        }

        private void OnMoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int rubbleId);
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            if (!shown.TryGetValue(rubbleId, out Shown entry) || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) return;

            Show(Lying(entry.Rubble.MovedTo(new System.Numerics.Vector3(x, y, z))));
        }

        private static RubblePiles Load(string path)
        {
            if (!File.Exists(path)) return new RubblePiles();

            try
            {
                return RubblePiles.LoadFromFile(path);
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"The rubble could not be loaded ({e.Message}): the world starts without any.");
                return new RubblePiles();
            }
        }

        private static void Write(FastBufferWriter writer, Rubble rubble)
        {
            writer.WriteValueSafe(rubble.Id);
            writer.WriteValueSafe(rubble.Position.X);
            writer.WriteValueSafe(rubble.Position.Y);
            writer.WriteValueSafe(rubble.Position.Z);
            writer.WriteValueSafe(rubble.Yaw);
            writer.WriteValueSafe(rubble.Block);
        }

        private static bool TryRead(FastBufferReader reader, out Rubble rubble)
        {
            reader.ReadValueSafe(out int id);
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            reader.ReadValueSafe(out float yaw);
            reader.ReadValueSafe(out ushort block);
            rubble = new Rubble(id, new System.Numerics.Vector3(x, y, z), yaw, block);
            return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(yaw) && Blocks.IsValid(block) && Blocks.TypeOf(block) != Blocks.Air;
        }
    }
}
