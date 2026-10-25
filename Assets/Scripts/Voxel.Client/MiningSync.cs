using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Mining and felling, a hit at a time. Each left click with the right tool is one hit, at most one
    // a swing (MiningRules.SwingInterval: three a second; a starving player cannot swing at all); a block
    // takes hits by its hardness, a tree by its wood, a stump a few shovel hits (MiningRules).
    //
    // The hitter's machine shows each hit at once (the swing, chips, the block's cracks, and a block's
    // break on its last hit) and tells the server (Mine.Hit). The server checks it (pace, reach, what
    // was hit, the tool owned, room for the block), counts it, burns the hit's calories, tells every
    // player (Mine.Struck: the hitter to reconcile, the others to show the hit), then on the last hit
    // breaks the block (WorldSync.TryBreak) or finishes the plant (EcosystemSync.FinishPlant: a tree
    // falls, leaving its stump and its logs). A refused hit is undone on the hitter's machine.
    //
    // Damage wears off when left alone (MiningRules.HitsAfterIdle), worked out on every machine
    // alike; it is never saved.
    public class MiningSync : MonoBehaviour
    {
        private const string HitMessage = "Mine.Hit";
        private const string StruckMessage = "Mine.Struck";

        private const byte BlockTarget = 0;
        private const byte PlantTarget = 1;

        private const byte AcceptedFlag = 1;
        private const byte YoursFlag = 2;
        private const byte FinishedFlag = 4;

        // A face index (ChunkMesher.FaceNormals) for a hit on a plant, whose chips fly out from its axis.
        private const byte NoFace = 255;

        private const int HitSize = sizeof(byte) + 3 * sizeof(int) + sizeof(ushort) + 3 * sizeof(float) + sizeof(byte);
        private const int StruckSize = HitSize + 3 * sizeof(byte);

        private const float SweepInterval = 1f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private HungerSync hungerSync;
        [SerializeField] private StorageSync storageSync;
        [SerializeField] private Material crackMaterial;
        [SerializeField] private Material chipMaterial;

        // Every machine: the hits shown on blocks and plants (on the hitter's, with its own hits the
        // server has not answered yet).
        private readonly MiningDamage<BlockPos> shownBlocks = new MiningDamage<BlockPos>();
        private readonly MiningDamage<int> shownPlants = new MiningDamage<int>();
        private readonly Dictionary<BlockPos, int> pendingBlocks = new Dictionary<BlockPos, int>();
        private readonly Dictionary<int, int> pendingPlants = new Dictionary<int, int>();
        private readonly List<BlockPos> spentBlocks = new List<BlockPos>();
        private readonly List<int> spentPlants = new List<int>();

        // Every machine: where the last hit shown on each plant landed, the side its notch is cut on.
        private readonly Dictionary<int, Vector3> plantHitPoints = new Dictionary<int, Vector3>();

        // Server side: the hits that count, and each player's pace.
        private readonly MiningDamage<BlockPos> serverBlocks = new MiningDamage<BlockPos>();
        private readonly MiningDamage<int> serverPlants = new MiningDamage<int>();
        private readonly Dictionary<ulong, HitPacer> pacers = new Dictionary<ulong, HitPacer>();

        private CrackViews cracks;
        private NotchViews notches;
        private ChipBursts chips;
        private float nextSwing;
        private float nextSweep;
        private bool isServer;
        private bool running;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        private static double Now => Time.unscaledTimeAsDouble;

        // Whether the local player can swing again now.
        public bool CanSwing => Time.time >= nextSwing;

        private void Awake()
        {
            if (Application.isBatchMode) return;

            if (crackMaterial != null) cracks = new CrackViews(crackMaterial);
            if (chipMaterial != null) chips = new ChipBursts(chipMaterial);
            notches = new NotchViews();
        }

        // To call right after the server or host has started.
        public void StartServer()
        {
            running = true;
            isServer = true;
            Messaging.RegisterNamedMessageHandler(HitMessage, OnHit);
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            running = true;
            Messaging.RegisterNamedMessageHandler(StruckMessage, OnStruck);
        }

        public void Stop()
        {
            if (!running) return;

            if (isServer) networkManager.OnClientDisconnectCallback -= OnClientDisconnected;

            shownBlocks.Clear();
            shownPlants.Clear();
            pendingBlocks.Clear();
            pendingPlants.Clear();
            serverBlocks.Clear();
            serverPlants.Clear();
            pacers.Clear();
            chips?.Clear();
            notches?.Clear();
            plantHitPoints.Clear();
            isServer = false;
            running = false;
        }

        // The local player swings now (with a tool, at something or nothing): false if the last swing
        // is not over. Their body swings for everyone.
        public bool TryStartSwing(PlayerModel model, bool starving)
        {
            if (!running || !CanSwing) return false;

            nextSwing = Time.time + MiningRules.SwingInterval(starving);
            model.Swing();
            return true;
        }

        // The local player's swing hits a block: aimed is the block they see there, face the struck
        // face (ChunkMesher.FaceNormals), point where the ray met it.
        public void HitBlock(BlockPos pos, ushort aimed, int face, Vector3 point)
        {
            if (!running) return;

            int needed = MiningRules.HitsToBreak(aimed);
            if (needed <= 0) return;

            bool last = shownBlocks.Hits(pos, Now) + 1 >= needed;
            SpawnChips(point, FaceNormal(face), BlockAtlas.ChipColour(aimed, face), last);
            if (isServer)
            {
                ServerHit(networkManager.LocalClientId, BlockTarget, pos, 0, aimed, point, (byte)face);
                return;
            }

            shownBlocks.AddHit(pos, Now);
            pendingBlocks[pos] = pendingBlocks.TryGetValue(pos, out int pending) ? pending + 1 : 1;
            if (last) worldSync.PredictBreak(pos);
            SendHit(BlockTarget, pos, 0, aimed, point, (byte)face);
        }

        // The local player's swing hits a plant shown here.
        public void HitPlant(int plantId, Vector3 point)
        {
            if (!running || !ecosystemSync.TryGetView(plantId, out PlantView view)) return;

            int needed = ecosystemSync.HitsNeeded(view);
            bool last = shownPlants.Hits(plantId, Now) + 1 >= needed;
            SpawnChips(point, PlantNormal(view, point), ecosystemSync.ChipColour(view.SpeciesIndex), last);
            plantHitPoints[plantId] = point;
            if (isServer)
            {
                ServerHit(networkManager.LocalClientId, PlantTarget, default, plantId, 0, point, NoFace);
                return;
            }

            shownPlants.AddHit(plantId, Now);
            pendingPlants[plantId] = pendingPlants.TryGetValue(plantId, out int pending) ? pending + 1 : 1;

            // A plant cut in one go goes at once; a tree falls only when the server says so.
            if (last && needed == 1) ecosystemSync.PredictRemove(plantId);
            SendHit(PlantTarget, default, plantId, 0, point, NoFace);
        }

        private void LateUpdate()
        {
            if (!running) return;

            World world = worldSync.World;
            double now = Now;
            if (Time.unscaledTime >= nextSweep)
            {
                nextSweep = Time.unscaledTime + SweepInterval;
                Sweep(world, now);
            }

            if (world == null) return;

            if (cracks != null)
            {
                foreach (BlockPos pos in shownBlocks.Keys)
                {
                    int needed = MiningRules.HitsToBreak(world.GetBlock(pos));
                    cracks.Draw(pos, MiningRules.CrackStage(shownBlocks.Hits(pos, now), needed));
                }
            }

            if (notches != null)
            {
                notches.BeginFrame();
                foreach (int plantId in shownPlants.Keys)
                {
                    int hits = shownPlants.Hits(plantId, now);
                    if (hits <= 0 || !ecosystemSync.TryGetView(plantId, out PlantView view)) continue;
                    if (!ecosystemSync.TryGetTrunk(view, out Vector3 centre, out Vector2 halfSize, out Material wood)) continue;

                    Vector3 toward = plantHitPoints.TryGetValue(plantId, out Vector3 point) ? point - centre : Vector3.forward;
                    notches.Draw(wood, centre, halfSize, toward, (float)hits / ecosystemSync.HitsNeeded(view));
                }
            }

            chips?.Render(world, Time.deltaTime);
        }

        // Forgets damage that wore off, and that of blocks broken and plants gone.
        private void Sweep(World world, double now)
        {
            shownBlocks.Sweep(now);
            shownPlants.Sweep(now);
            serverBlocks.Sweep(now);
            serverPlants.Sweep(now);

            spentBlocks.Clear();
            foreach (BlockPos pos in shownBlocks.Keys)
            {
                if (world == null || MiningRules.HitsToBreak(world.GetBlock(pos)) == 0) spentBlocks.Add(pos);
            }

            foreach (BlockPos pos in spentBlocks)
            {
                shownBlocks.Remove(pos);
                pendingBlocks.Remove(pos);
            }

            spentPlants.Clear();
            foreach (int plantId in shownPlants.Keys)
            {
                if (!ecosystemSync.TryGetView(plantId, out _)) spentPlants.Add(plantId);
            }

            foreach (int plantId in spentPlants)
            {
                shownPlants.Remove(plantId);
                pendingPlants.Remove(plantId);
            }

            spentPlants.Clear();
            foreach (int plantId in plantHitPoints.Keys)
            {
                if (shownPlants.Hits(plantId, now) == 0) spentPlants.Add(plantId);
            }

            foreach (int plantId in spentPlants)
            {
                plantHitPoints.Remove(plantId);
            }
        }

        // ---- Server ----

        private void ServerHit(ulong clientId, byte kind, BlockPos pos, int plantId, ushort seen, Vector3 point, byte face)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return;
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)) return;

            double now = Now;
            pacers.TryGetValue(clientId, out HitPacer pacer);
            bool paced = pacer.TryHit(now, MiningRules.SwingInterval(false) * 0.9);
            pacers[clientId] = pacer;

            int needed;
            float calories;
            Vector3 min, max;
            ushort type = 0;
            bool valid;
            if (kind == BlockTarget)
            {
                valid = CheckBlockHit(clientId, client, pos, seen, out type, out needed, out calories);
                min = new Vector3(pos.X, pos.Y, pos.Z);
                max = min + Vector3.one;
            }
            else
            {
                valid = ecosystemSync.CheckPlantHit(clientId, plantId, out needed, out calories, out min, out max);
                if (valid && ecosystemSync.TryGetView(plantId, out PlantView view)) type = (ushort)view.SpeciesIndex;
            }

            // A starving player swings at nothing: what they predicted comes back.
            if (!paced || !valid || hungerSync.IsStarving(clientId))
            {
                Refuse(clientId, kind, pos, plantId, now);
                return;
            }

            int hits = kind == BlockTarget ? serverBlocks.AddHit(pos, now) : serverPlants.AddHit(plantId, now);
            hungerSync.BurnEffort(clientId, calories);
            bool finished = hits >= needed;

            // Told before the block breaks, so that the others colour the chips while they still know it.
            point = Vector3.Max(min, Vector3.Min(max, point));
            Broadcast(clientId, kind, pos, plantId, type, (byte)(AcceptedFlag | (finished ? FinishedFlag : 0)), hits, needed, point, face);

            if (!finished) return;

            if (kind == BlockTarget)
            {
                serverBlocks.Remove(pos);
                if (!worldSync.TryBreak(clientId, pos))
                {
                    serverBlocks.Set(pos, needed - 1, now);
                    worldSync.SendRealBlock(clientId, pos);
                }
            }
            else
            {
                serverPlants.Remove(plantId);
                ecosystemSync.FinishPlant(clientId, plantId);
            }
        }

        // A hit on a block counts if it is within reach, still the block the hitter saw, breakable
        // with a tool they own, and they have room for it.
        private bool CheckBlockHit(ulong clientId, NetworkClient client, BlockPos pos, ushort seen, out ushort type, out int needed, out float calories)
        {
            World world = worldSync.World;
            type = world != null ? world.GetBlock(pos) : (ushort)0;
            needed = MiningRules.HitsToBreak(type);
            calories = HungerRules.PerHit(needed);
            if (world == null || needed <= 0 || Blocks.TypeOf(type) != Blocks.TypeOf(seen)) return false;

            // What storage stands on stays (the player is told before swinging, PlayerController).
            if (storageSync != null && storageSync.StandsOn(pos)) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            if (!BlockEditRules.IsAllowed(world, eyes.ToNumerics(), pos, Blocks.Air)) return false;

            // On someone else's land no hit counts (the player is told whose it is).
            if (!worldSync.CanBreakOnClaims(clientId, pos)) return false;

            int tool = ToolRules.ToBreak(type);
            if (tool != Items.None && !inventorySync.Owns(clientId, tool)) return false;

            int drop = Items.DropOf(type);
            if (inventorySync.RoomFor(clientId, drop) > 0) return true;

            inventorySync.TellFull(clientId, InventorySync.ReasonFor(drop));
            return false;
        }

        // A refused hit: the hitter is told the hits that count, and gets back what it removed early.
        private void Refuse(ulong clientId, byte kind, BlockPos pos, int plantId, double now)
        {
            if (clientId == networkManager.LocalClientId) return;

            int hits = kind == BlockTarget ? serverBlocks.Hits(pos, now) : serverPlants.Hits(plantId, now);
            SendStruck(clientId, kind, pos, plantId, 0, YoursFlag, hits, 0, Vector3.zero, NoFace);
            if (kind == BlockTarget) worldSync.SendRealBlock(clientId, pos);
            else ecosystemSync.RestorePlantTo(clientId, plantId);
        }

        // Every player hears of an accepted hit; the host shows it here.
        private void Broadcast(ulong hitter, byte kind, BlockPos pos, int plantId, ushort type, byte flags, int hits, int needed, Vector3 point, byte face)
        {
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId == networkManager.LocalClientId) continue;

                SendStruck(clientId, kind, pos, plantId, type, (byte)(flags | (clientId == hitter ? YoursFlag : 0)), hits, needed, point, face);
            }

            if (networkManager.IsHost) ShowStruck(kind, pos, plantId, type, (byte)(flags | (hitter == networkManager.LocalClientId ? YoursFlag : 0)), hits, needed, point, face, true);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            pacers.Remove(clientId);
        }

        private void OnHit(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int y);
            reader.ReadValueSafe(out int z);
            reader.ReadValueSafe(out ushort seen);
            Vector3 point = ReadVector(reader);
            reader.ReadValueSafe(out byte face);
            if (kind == BlockTarget) ServerHit(senderId, kind, new BlockPos(x, y, z), 0, seen, point, face);
            else if (kind == PlantTarget) ServerHit(senderId, kind, default, x, 0, point, NoFace);
        }

        // ---- Client ----

        private void OnStruck(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte kind);
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int y);
            reader.ReadValueSafe(out int z);
            reader.ReadValueSafe(out ushort type);
            Vector3 point = ReadVector(reader);
            reader.ReadValueSafe(out byte face);
            reader.ReadValueSafe(out byte flags);
            reader.ReadValueSafe(out byte hits);
            reader.ReadValueSafe(out byte needed);
            ShowStruck(kind, new BlockPos(x, y, z), x, type, flags, hits, needed, point, face, false);
        }

        // A hit the server counted (or refused, for the hitter). The hitter shows the hits that count
        // plus its own still unanswered; the others show the hit as it came.
        private void ShowStruck(byte kind, BlockPos pos, int plantId, ushort type, byte flags, int hits, int needed, Vector3 point, byte face, bool host)
        {
            double now = Now;
            bool yours = (flags & YoursFlag) != 0;
            if (kind == BlockTarget)
            {
                int pending = 0;
                if (yours && !host && pendingBlocks.TryGetValue(pos, out pending))
                {
                    pending--;
                    if (pending > 0) pendingBlocks[pos] = pending;
                    else pendingBlocks.Remove(pos);
                }

                shownBlocks.Set(pos, (flags & FinishedFlag) != 0 ? 0 : hits + pending, now);
                if (!yours && (flags & AcceptedFlag) != 0 && face < ChunkMesher.FaceCount)
                {
                    SpawnChips(point, FaceNormal(face), BlockAtlas.ChipColour(type, face), (flags & FinishedFlag) != 0);
                }
            }
            else if (kind == PlantTarget)
            {
                int pending = 0;
                if (yours && !host && pendingPlants.TryGetValue(plantId, out pending))
                {
                    pending--;
                    if (pending > 0) pendingPlants[plantId] = pending;
                    else pendingPlants.Remove(plantId);
                }

                shownPlants.Set(plantId, (flags & FinishedFlag) != 0 ? 0 : hits + pending, now);
                if (!yours && (flags & AcceptedFlag) != 0 && ecosystemSync.TryGetView(plantId, out PlantView view))
                {
                    SpawnChips(point, PlantNormal(view, point), ecosystemSync.ChipColour(type), (flags & FinishedFlag) != 0);
                    plantHitPoints[plantId] = point;
                }
            }
        }

        private void SpawnChips(Vector3 point, Vector3 normal, Color colour, bool last)
        {
            chips?.Spawn(point, normal, colour, last ? ChipBursts.ChipsPerBreak : ChipBursts.ChipsPerHit);
        }

        private static Vector3 FaceNormal(int face)
        {
            return face >= 0 && face < ChunkMesher.FaceCount ? (Vector3)ChunkMesher.FaceNormals[face] : Vector3.up;
        }

        // Chips fly out of a plant's trunk, away from its axis.
        private static Vector3 PlantNormal(PlantView view, Vector3 point)
        {
            var away = new Vector3(point.x - (view.X + 0.5f), 0f, point.z - (view.Z + 0.5f));
            return away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.up;
        }

        // ---- Messages ----

        private void SendHit(byte kind, BlockPos pos, int plantId, ushort seen, Vector3 point, byte face)
        {
            using (var writer = new FastBufferWriter(HitSize, Allocator.Temp))
            {
                WriteTarget(writer, kind, pos, plantId, seen);
                WriteVector(writer, point);
                writer.WriteValueSafe(face);
                Messaging.SendNamedMessage(HitMessage, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private void SendStruck(ulong clientId, byte kind, BlockPos pos, int plantId, ushort type, byte flags, int hits, int needed, Vector3 point, byte face)
        {
            using (var writer = new FastBufferWriter(StruckSize, Allocator.Temp))
            {
                WriteTarget(writer, kind, pos, plantId, type);
                WriteVector(writer, point);
                writer.WriteValueSafe(face);
                writer.WriteValueSafe(flags);
                writer.WriteValueSafe((byte)Mathf.Clamp(hits, 0, 255));
                writer.WriteValueSafe((byte)Mathf.Clamp(needed, 0, 255));
                Messaging.SendNamedMessage(StruckMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        // A block by its position and type, or a plant by its id (in the first int) and species.
        private static void WriteTarget(FastBufferWriter writer, byte kind, BlockPos pos, int plantId, ushort type)
        {
            writer.WriteValueSafe(kind);
            writer.WriteValueSafe(kind == BlockTarget ? pos.X : plantId);
            writer.WriteValueSafe(kind == BlockTarget ? pos.Y : 0);
            writer.WriteValueSafe(kind == BlockTarget ? pos.Z : 0);
            writer.WriteValueSafe(type);
        }

        private static void WriteVector(FastBufferWriter writer, Vector3 v)
        {
            writer.WriteValueSafe(v.x);
            writer.WriteValueSafe(v.y);
            writer.WriteValueSafe(v.z);
        }

        private static Vector3 ReadVector(FastBufferReader reader)
        {
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            reader.ReadValueSafe(out float z);
            return new Vector3(x, y, z);
        }
    }
}
