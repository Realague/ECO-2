using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The logs felled trees leave on the ground, each worth MiningRules.WoodPerLog wood. The server
    // keeps them (LyingLogs) and saves them with the world (logs.dat), drops a tree's logs along where
    // it fell (LogPlacement.AlongFall, shown once it has hit the ground), keeps them resting on the
    // ground when blocks change under or into them, and gives one to a player picking it up with a
    // right click if they can carry it. Every machine with a display draws them (LogViews).
    //
    // It also keeps the trees lying felled (FallenTrees, fallen.dat): a felled tree lies with its
    // branches (drawn by PlantRenderer, lying as it fell) until they are pruned with the axe, then as a
    // row of logs (drawn as logs) bucked off one at a time from its top, each becoming a log lying on
    // the ground. Skills may do either step at once (SkillInteractions.AutoPrune, AutoBuck). A block
    // changing under or into a fallen tree breaks it into logs at once.
    public class LogSync : MonoBehaviour
    {
        private const string AllMessage = "Log.All";
        private const string AddedMessage = "Log.Added";
        private const string RemovedMessage = "Log.Removed";
        private const string MovedMessage = "Log.Moved";
        private const string PickupMessage = "Log.Pickup";
        private const string FallenAllMessage = "Fallen.All";
        private const string FallenAddedMessage = "Fallen.Added";
        private const string FallenChangedMessage = "Fallen.Changed";
        private const string FallenRemovedMessage = "Fallen.Removed";
        private const string SaveFileName = "logs.dat";
        private const string FallenFileName = "fallen.dat";

        // How close a pointer ray must pass to a fallen tree with its branches, around each log of it.
        private const float CrownHalfWidth = 0.9f;
        private const float CrownHeight = 1.4f;

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
        [SerializeField] private SkillSync skillSync;
        [SerializeField] private Material cutMaterial;

        private struct Shown
        {
            public LyingLog Log;

            // A falling tree's logs show up once it has hit the ground.
            public float VisibleAt;
        }

        // Every machine: the logs lying in the world.
        private readonly Dictionary<int, Shown> shown = new Dictionary<int, Shown>();

        // Every machine: the trees lying felled (on the host, the server's own), and when each shows.
        private FallenTrees fallen = new FallenTrees();
        private readonly Dictionary<int, float> fallenVisibleAt = new Dictionary<int, float>();
        private string fallenPath;
        private readonly List<int> fallenInColumn = new List<int>();

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

            fallenPath = Path.Combine(directory, FallenFileName);
            fallen = LoadFallen(fallenPath, ecosystemSync.PlantAssets.Count);
            foreach (FallenTree tree in fallen.All) ShowFallen(tree, 0f);

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
            Messaging.RegisterNamedMessageHandler(FallenAllMessage, OnFallenAll);
            Messaging.RegisterNamedMessageHandler(FallenAddedMessage, OnFallenAdded);
            Messaging.RegisterNamedMessageHandler(FallenChangedMessage, OnFallenChanged);
            Messaging.RegisterNamedMessageHandler(FallenRemovedMessage, OnFallenRemoved);
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
            fallen = new FallenTrees();
            fallenVisibleAt.Clear();
            running = false;
        }

        // Server side: writes the logs lying in the world, and the fallen trees, next to the other saves.
        public void Save()
        {
            if (store == null) return;

            try
            {
                store.SaveToFile(savePath);
                fallen.SaveToFile(fallenPath);
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

        // ---- Fallen trees ----

        // Server side: a felled tree lying where it fell, its count logs placed along the fall (as
        // DropAlongFall would lay them), with these branches; shown after delay seconds. Felled by a
        // player whose skills buck a pruned tree at once, it is bucked at once if it has no branches.
        internal void AddFallen(ulong feller, PlantView plant, float yaw, Vector3 foot, Vector3 direction, float start, float length, int count, int branches, float delay)
        {
            if (!IsServer || serverWorld == null || count <= 0) return;

            LogPlacement.AlongFall(serverWorld, foot.ToNumerics(), direction.ToNumerics(), start, length, count, placed);
            var segments = new List<FallenSegment>();
            foreach ((System.Numerics.Vector3 position, float logYaw) in placed) segments.Add(new FallenSegment(position, logYaw));

            FallenTree tree = fallen.Add(plant.SpeciesIndex, plant.Stage, plant.X, plant.Y, plant.Z, yaw, branches, segments);
            if (tree.Branches == 0 && skillSync != null && skillSync.Has(feller, SkillBonusKind.Interaction, SkillInteractions.AutoBuck))
            {
                BuckAll(tree, delay);
                return;
            }

            ShowFallen(tree, delay);
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                SendFallen(FallenAddedMessage, clientId, writer =>
                {
                    writer.WriteValueSafe(delay);
                    WriteFallen(writer, tree);
                });
            }
        }

        public bool HasFallen(int fallenId) => fallen.TryGet(fallenId, out _);

        public bool TryGetFallen(int fallenId, out FallenTree tree) => fallen.TryGet(fallenId, out tree);

        // The SkillContext of work on a fallen tree: pruning it or bucking it.
        public string SkillContextOf(FallenTree tree)
        {
            string species = (uint)tree.Species < (uint)ecosystemSync.PlantAssets.Count ? ecosystemSync.PlantAssets[tree.Species].name : tree.Species.ToString();
            return SkillContext.Of(SkillContext.Fell, species, tree.Branches > 0 ? SkillContext.Prune : SkillContext.Buck);
        }

        // The axe hits the next part of a fallen tree takes for the local player, with their skills.
        public int LocalHitsNeeded(FallenTree tree)
        {
            int hits = FallenTreeRules.HitsFor(tree);
            return skillSync != null ? SkillBonuses.Hits(hits, skillSync.LocalBonus(SkillBonusKind.TimePercent, SkillContextOf(tree))) : hits;
        }

        // What a fallen tree is, for the aiming label: "Fallen oak · 3 branches", "Fallen oak · 4 logs".
        public string DescribeFallen(int fallenId)
        {
            if (!fallen.TryGet(fallenId, out FallenTree tree) || (uint)tree.Species >= (uint)ecosystemSync.PlantAssets.Count) return null;

            string species = EcosystemSync.DisplayName(ecosystemSync.PlantAssets[tree.Species].name);
            string name = Loc.T("Fallen {1}", Loc.Name(species), Loc.Lower(species));
            return tree.Branches > 0
                ? Loc.T("{0} · {1} branches", name, tree.Branches)
                : Loc.T("{0} · {1} logs", name, tree.Segments.Count);
        }

        // The fallen tree the local player points at, at most maxDistance away: around each of its
        // logs, wide and high while it has its branches, as a log once pruned.
        public bool TryAimedFallen(Vector3 origin, Vector3 direction, float maxDistance, out int fallenId, out float distance)
        {
            fallenId = 0;
            distance = maxDistance;
            float now = Time.time;
            System.Numerics.Vector3 from = origin.ToNumerics();
            System.Numerics.Vector3 to = (origin + direction * maxDistance).ToNumerics();
            foreach (FallenTree tree in fallen.All)
            {
                if (fallenVisibleAt.TryGetValue(tree.Id, out float visibleAt) && visibleAt > now) continue;

                float half = tree.Branches > 0 ? CrownHalfWidth : PickHalfWidth;
                float height = tree.Branches > 0 ? CrownHeight : PickHeight;
                foreach (FallenSegment segment in tree.Segments)
                {
                    System.Numerics.Vector3 position = segment.Position;
                    var min = new System.Numerics.Vector3(position.X - half, position.Y, position.Z - half);
                    var max = new System.Numerics.Vector3(position.X + half, position.Y + height, position.Z + half);
                    float along = ArrowFlight.SegmentEntry(from, to, min, max);
                    if (along >= 0f && along < distance)
                    {
                        distance = along;
                        fallenId = tree.Id;
                    }
                }
            }

            return fallenId != 0;
        }

        // Server side: a hit on a fallen tree counts if it is within reach, not on someone else's land,
        // and the player owns an axe; needed is the hits of its next part (with the player's skills),
        // calories one axe hit's (before skills), context what the skills see of it.
        internal bool CheckFallenHit(ulong clientId, int fallenId, out int needed, out float calories, out string context, out int species, out Vector3 min, out Vector3 max)
        {
            needed = 0;
            calories = HungerRules.PerAxeHit;
            context = null;
            species = 0;
            min = max = Vector3.zero;
            if (!IsServer || !fallen.TryGet(fallenId, out FallenTree tree) || tree.Segments.Count == 0) return false;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + CrownHalfWidth + 1f;
            bool near = false;
            foreach (FallenSegment segment in tree.Segments)
            {
                if ((segment.Position.ToUnity() - eyes).sqrMagnitude <= reach * reach) near = true;
            }

            if (!near || !claimSync.Check(clientId, tree.X, tree.Z) || !inventorySync.Owns(clientId, Items.Axe)) return false;

            context = SkillContextOf(tree);
            species = tree.Species;
            needed = FallenTreeRules.HitsFor(tree);
            FallenSegment top = tree.Segments[tree.Segments.Count - 1];
            float half = tree.Branches > 0 ? CrownHalfWidth : PickHalfWidth;
            min = top.Position.ToUnity() - new Vector3(half + 20f, 1f, half + 20f);
            max = top.Position.ToUnity() + new Vector3(half + 20f, CrownHeight + 1f, half + 20f);
            return true;
        }

        // Server side: the last hit of a fallen tree's next part: a branch comes off (and, the last one
        // off, a player whose skills buck at once has its logs fall apart), or its top log comes off and
        // lies on the ground.
        internal void FinishFallenPart(ulong clientId, int fallenId)
        {
            if (!IsServer || !fallen.TryGet(fallenId, out FallenTree tree)) return;

            if (tree.Branches > 0)
            {
                fallen.Prune(fallenId);
                if (tree.Branches == 0 && skillSync != null && skillSync.Has(clientId, SkillBonusKind.Interaction, SkillInteractions.AutoBuck))
                {
                    BuckAll(tree, 0f);
                    return;
                }

                if (tree.Branches == 0) ecosystemSync.EndLying(fallenId);
                BroadcastFallenChanged(tree);
                return;
            }

            if (!fallen.Buck(fallenId, out FallenSegment log)) return;

            DropLog(log, tree.Species);
            if (fallen.TryGet(fallenId, out _)) BroadcastFallenChanged(tree);
            else RemoveFallen(fallenId, false);
        }

        // Server side: the whole trunk falls apart into logs where they lie, its branches lost.
        private void BuckAll(FallenTree tree, float delay)
        {
            added.Clear();
            foreach (FallenSegment segment in tree.Segments) added.Add(store.Add(Settled(segment.Position), segment.Yaw, tree.Species));
            Publish(added, delay);
            RemoveFallen(tree.Id, true);
        }

        private void DropLog(FallenSegment log, int species)
        {
            added.Clear();
            added.Add(store.Add(Settled(log.Position), log.Yaw, species));
            Publish(added, 0f);
        }

        private System.Numerics.Vector3 Settled(System.Numerics.Vector3 position) =>
            serverWorld != null && LogPlacement.Settle(serverWorld, position, out System.Numerics.Vector3 settled) ? settled : position;

        private void RemoveFallen(int fallenId, bool removeFromStore)
        {
            if (removeFromStore) fallen.Remove(fallenId);
            fallenVisibleAt.Remove(fallenId);
            ecosystemSync.EndLying(fallenId);
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                SendFallen(FallenRemovedMessage, clientId, writer => writer.WriteValueSafe(fallenId));
            }
        }

        private void BroadcastFallenChanged(FallenTree tree)
        {
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                SendFallen(FallenChangedMessage, clientId, writer =>
                {
                    writer.WriteValueSafe(tree.Id);
                    writer.WriteValueSafe(tree.Branches);
                    writer.WriteValueSafe(tree.Segments.Count);
                });
            }
        }

        // This machine: a fallen tree to show, lying with its branches (PlantRenderer) until pruned.
        private void ShowFallen(FallenTree tree, float delay)
        {
            fallenVisibleAt[tree.Id] = Time.time + delay;
            if (tree.Branches > 0) ecosystemSync.ShowLying(tree.Id, ViewOf(tree), tree.Yaw);
        }

        private static PlantView ViewOf(FallenTree tree) => new PlantView { SpeciesIndex = tree.Species, Stage = tree.Stage, X = tree.X, Y = tree.Y, Z = tree.Z };

        private delegate void FallenWriter(FastBufferWriter writer);

        private void SendFallen(string message, ulong clientId, FallenWriter write)
        {
            using (var writer = new FastBufferWriter(256, Allocator.Temp, 1 << 24))
            {
                write(writer);
                Messaging.SendNamedMessage(message, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void WriteFallen(FastBufferWriter writer, FallenTree tree)
        {
            writer.WriteValueSafe(tree.Id);
            writer.WriteValueSafe((ushort)tree.Species);
            writer.WriteValueSafe((byte)tree.Stage);
            writer.WriteValueSafe(tree.X);
            writer.WriteValueSafe(tree.Y);
            writer.WriteValueSafe(tree.Z);
            writer.WriteValueSafe(tree.Yaw);
            writer.WriteValueSafe(tree.Branches);
            writer.WriteValueSafe(tree.Segments.Count);
            foreach (FallenSegment segment in tree.Segments)
            {
                writer.WriteValueSafe(segment.Position.X);
                writer.WriteValueSafe(segment.Position.Y);
                writer.WriteValueSafe(segment.Position.Z);
                writer.WriteValueSafe(segment.Yaw);
            }
        }

        private bool TryReadFallen(FastBufferReader reader, out FallenTree tree)
        {
            tree = new FallenTree();
            reader.ReadValueSafe(out tree.Id);
            reader.ReadValueSafe(out ushort species);
            reader.ReadValueSafe(out byte stage);
            reader.ReadValueSafe(out tree.X);
            reader.ReadValueSafe(out tree.Y);
            reader.ReadValueSafe(out tree.Z);
            reader.ReadValueSafe(out tree.Yaw);
            reader.ReadValueSafe(out tree.Branches);
            reader.ReadValueSafe(out int count);
            tree.Species = species;
            tree.Stage = stage;
            bool valid = species < ecosystemSync.PlantAssets.Count && count >= 0 && count <= 1000 && float.IsFinite(tree.Yaw);
            for (int i = 0; i < count && i <= 1000; i++)
            {
                reader.ReadValueSafe(out float x);
                reader.ReadValueSafe(out float y);
                reader.ReadValueSafe(out float z);
                reader.ReadValueSafe(out float yaw);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(yaw)) valid = false;
                tree.Segments.Add(new FallenSegment(new System.Numerics.Vector3(x, y, z), yaw));
            }

            return valid;
        }

        private void OnFallenAll(ulong senderId, FastBufferReader reader)
        {
            foreach (FallenTree tree in fallen.All) ecosystemSync.EndLying(tree.Id);
            fallen.Clear();
            fallenVisibleAt.Clear();
            reader.ReadValueSafe(out int count);
            for (int i = 0; i < count; i++)
            {
                if (!TryReadFallen(reader, out FallenTree tree)) continue;

                fallen.Put(tree);
                ShowFallen(tree, 0f);
            }
        }

        private void OnFallenAdded(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out float delay);
            if (!TryReadFallen(reader, out FallenTree tree)) return;

            fallen.Put(tree);
            ShowFallen(tree, float.IsFinite(delay) ? Mathf.Clamp(delay, 0f, 5f) : 0f);
        }

        private void OnFallenChanged(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int fallenId);
            reader.ReadValueSafe(out int branches);
            reader.ReadValueSafe(out int logs);
            if (!fallen.TryGet(fallenId, out FallenTree tree)) return;

            tree.Branches = Mathf.Max(0, branches);
            if (logs >= 0 && logs < tree.Segments.Count) tree.Segments.RemoveRange(logs, tree.Segments.Count - logs);
            if (tree.Branches == 0) ecosystemSync.EndLying(fallenId);
        }

        private void OnFallenRemoved(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int fallenId);
            fallen.Remove(fallenId);
            fallenVisibleAt.Remove(fallenId);
            ecosystemSync.EndLying(fallenId);
        }

        // Fallen trees of a species this game does not know are dropped.
        private static FallenTrees LoadFallen(string path, int speciesCount)
        {
            if (!File.Exists(path)) return new FallenTrees();

            try
            {
                FallenTrees loaded = FallenTrees.LoadFromFile(path);
                var unknown = new List<int>();
                foreach (FallenTree tree in loaded.All)
                {
                    if (tree.Species >= speciesCount) unknown.Add(tree.Id);
                }

                foreach (int id in unknown) loaded.Remove(id);
                return loaded;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"The fallen trees could not be loaded ({e.Message}): the world starts without any.");
                return new FallenTrees();
            }
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
            if (!running || Application.isBatchMode || (shown.Count == 0 && fallen.Count == 0)) return;

            if (views == null) views = new LogViews(ecosystemSync.PlantAssets, cutMaterial);

            float now = Time.time;
            World world = TerrainSettings.Smooth ? worldSync.World : null;
            foreach (Shown entry in shown.Values)
            {
                if (entry.VisibleAt <= now) views.Add(OnShownGround(world, entry.Log.Position.ToUnity()), entry.Log.Yaw, entry.Log.Species);
            }

            // A pruned fallen tree is its row of logs, still to be bucked off.
            foreach (FallenTree tree in fallen.All)
            {
                if (tree.Branches > 0 || (fallenVisibleAt.TryGetValue(tree.Id, out float visibleAt) && visibleAt > now)) continue;

                foreach (FallenSegment segment in tree.Segments) views.Add(OnShownGround(world, segment.Position.ToUnity()), segment.Yaw, tree.Species);
            }

            views.Render();
        }

        // On smoothed ground (a world given), a log lying on a block's top rests at the height the ground
        // shows there (SmoothTerrain.HeightAt); afloat or on anything else, where it is.
        private static Vector3 OnShownGround(World world, Vector3 position)
        {
            if (world == null) return position;

            int x = Mathf.FloorToInt(position.x), y = Mathf.RoundToInt(position.y), z = Mathf.FloorToInt(position.z);
            if (Mathf.Abs(position.y - y) > 0.01f || !world.InBounds(x, y - 1, z) || !SmoothTerrain.IsSmooth(world.GetBlock(x, y - 1, z))) return position;

            position.y = SmoothTerrain.HeightAt(world, x, z, y, position.x - x, position.z - z);
            return position;
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

            // A fallen tree a block changed under or into falls apart into logs, its branches lost.
            fallen.InColumn(pos.X, pos.Z, fallenInColumn);
            foreach (int id in fallenInColumn)
            {
                if (!fallen.TryGet(id, out FallenTree tree)) continue;

                bool near = tree.X == pos.X && tree.Z == pos.Z && Mathf.Abs(tree.Y - pos.Y) <= SettleRange + 1;
                foreach (FallenSegment segment in tree.Segments)
                {
                    if (Mathf.FloorToInt(segment.Position.X) == pos.X && Mathf.FloorToInt(segment.Position.Z) == pos.Z && Mathf.Abs(segment.Position.Y - pos.Y) <= SettleRange + 1) near = true;
                }

                if (near) BuckAll(tree, 0f);
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

            SendFallen(FallenAllMessage, clientId, writer =>
            {
                writer.WriteValueSafe(fallen.Count);
                foreach (FallenTree tree in fallen.All) WriteFallen(writer, tree);
            });
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
