using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // What a player's accepted edit did (WorldSync.EditApplied).
    public enum EditKind
    {
        Placed,
        Broken,
        Tilled,

        // A construction block taken back with a hammer (as well as Broken).
        TakenBack
    }

    // Synchronises the world between the server and the clients with named messages.
    // Server: sends the modified chunks to each joining client, validates block edit requests
    // and broadcasts every change. Client: rebuilds the received world and applies the changes.
    // Blocks are placed through edit requests here; they are broken only by hits (MiningSync), whose
    // last one calls TryBreak.
    public class WorldSync : MonoBehaviour
    {
        private const string WorldInfoMessage = "World.Info";
        private const string ChunksMessage = "World.Chunks";
        private const string SetBlockRequestMessage = "World.SetBlockRequest";
        private const string TillRequestMessage = "World.Till";

        // A door set up (its lower half, closed, with its facing) or opened and closed (Blocks.Air).
        private const string DoorRequestMessage = "World.Door";

        // A hammer builds a line of construction blocks (both ends and the block), or takes one back.
        private const string BuildRequestMessage = "World.Build";
        private const string UnbuildRequestMessage = "World.Unbuild";

        // Server side: one hammer action a player at most this often, a line counting as one.
        private const float HammerInterval = 0.2f;
        private const string BlockChangedMessage = "World.BlockChanged";

        // One batch of chunks per client and per network tick, so as not to flood the connection.
        private const int MaxBatchBytes = 16 * 1024;
        private const int BlockMessageSize = 3 * sizeof(int) + sizeof(ushort);
        private const int WorldInfoSize = 6 * sizeof(int) + sizeof(ulong);
        private const int WaterBlocksPerFrame = 64;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private VoxelWorld voxelWorld;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private StorageSync storageSync;
        [SerializeField] private ClaimSync claimSync;

        private readonly Dictionary<ulong, ChunkStream> streams = new Dictionary<ulong, ChunkStream>();
        private readonly List<ulong> finishedStreams = new List<ulong>();
        private readonly List<Chunk> receivedChunks = new List<Chunk>();

        // Server side: blocks water is pouring into (WaterFill), taken a few at a time so that a
        // hole fills up visibly and the changes do not all go out on the same tick.
        private readonly Queue<BlockPos> pendingWater = new Queue<BlockPos>();
        private readonly List<BlockPos> fill = new List<BlockPos>();
        private readonly List<BlockPos> heldTorches = new List<BlockPos>();
        private World serverWorld;
        private bool running;
        private bool ready;
        private int expectedChunkCount;
        private int receivedChunkCount;

        // Server side: a player's edit was accepted (their client id, and what it did).
        public event Action<ulong, EditKind> EditApplied;

        // The world of the game, or null until it is fully available on this machine.
        public World World => ready ? voxelWorld.World : null;

        public float LoadingProgress => expectedChunkCount > 0 ? (float)receivedChunkCount / expectedChunkCount : 0f;

        // Reason why this client left the server on its own, or null. Kept after the session
        // stops so it can be displayed, cleared at the next start.
        public string Failure { get; private set; }

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // To call right after the server or host has started, once its world is loaded.
        public void StartServer()
        {
            Failure = null;
            running = true;
            ready = true;
            serverWorld = voxelWorld.World;
            serverWorld.BlockChanged += BroadcastBlockChange;

            Messaging.RegisterNamedMessageHandler(SetBlockRequestMessage, OnSetBlockRequest);
            Messaging.RegisterNamedMessageHandler(TillRequestMessage, OnTillRequest);
            Messaging.RegisterNamedMessageHandler(DoorRequestMessage, OnDoorRequest);
            Messaging.RegisterNamedMessageHandler(BuildRequestMessage, OnBuildRequest);
            Messaging.RegisterNamedMessageHandler(UnbuildRequestMessage, OnUnbuildRequest);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.NetworkTickSystem.Tick += SendChunkBatches;
        }

        // To call right after a client's connection has been launched.
        public void StartClient()
        {
            Failure = null;
            running = true;
            Messaging.RegisterNamedMessageHandler(WorldInfoMessage, OnWorldInfo);
            Messaging.RegisterNamedMessageHandler(ChunksMessage, OnChunks);
            Messaging.RegisterNamedMessageHandler(BlockChangedMessage, OnBlockChanged);
        }

        public void Stop()
        {
            if (!running) return;

            if (serverWorld != null)
            {
                serverWorld.BlockChanged -= BroadcastBlockChange;
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
                if (networkManager.NetworkTickSystem != null) networkManager.NetworkTickSystem.Tick -= SendChunkBatches;
            }

            streams.Clear();
            pendingWater.Clear();
            serverWorld = null;
            running = false;
            ready = false;
            expectedChunkCount = 0;
            receivedChunkCount = 0;
        }

        // A block placed by the local player. The server decides; a client applies it right away
        // for responsiveness, and the server corrects it if it refuses.
        public void RequestSetBlock(BlockPos pos, ushort block)
        {
            if (!ready || !Blocks.IsPlaceable(block)) return;

            if (serverWorld != null)
            {
                TryApplyEdit(networkManager.LocalClientId, pos, block);
                return;
            }

            voxelWorld.World.SetBlock(pos, block);
            SendBlock(SetBlockRequestMessage, NetworkManager.ServerClientId, pos, block);
        }

        // Placing only: a block is broken by hits (MiningSync), never by a request to set air. A
        // construction block needs a hammer (extraReach: for the cells of a line built with it).
        private bool TryApplyEdit(ulong clientId, BlockPos pos, ushort block, float extraReach = 0f)
        {
            if (inventorySync.IsStarving(clientId)) return false;

            if (!Blocks.IsPlaceable(block) || !IsEditAllowed(clientId, pos, block, extraReach)) return false;
            if (Blocks.IsConstruction(block) && !inventorySync.Owns(clientId, Items.Hammer)) return false;
            if (!claimSync.Check(clientId, pos.X, pos.Z)) return false;

            // Placing a block takes it from the player's inventory (not in creative mode).
            if (!inventorySync.TryConsumePlaced(clientId, Items.OfBlock(block))) return false;

            serverWorld.SetBlock(pos, block);
            EditApplied?.Invoke(clientId, EditKind.Placed);
            return true;
        }

        // Server side: the last hit on a block breaks it (MiningSync). It goes to the player's
        // inventory, so a player who cannot carry it cannot break it; and it needs the right tool,
        // which the server cannot see in hand (the hotbar's choice is the client's): the player must
        // at least own it.
        internal bool TryBreak(ulong clientId, BlockPos pos, float extraReach = 0f)
        {
            if (serverWorld == null || !IsEditAllowed(clientId, pos, Blocks.Air, extraReach)) return false;
            if (!CanBreakOnClaims(clientId, pos)) return false;

            ushort previous = serverWorld.GetBlock(pos);
            int tool = ToolRules.ToBreak(previous);
            if (tool != Items.None && !inventorySync.Owns(clientId, tool)) return false;

            int drop = Items.DropOf(previous);
            if (inventorySync.RoomFor(clientId, drop) <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(drop));
                return false;
            }

            serverWorld.SetBlock(pos, Blocks.Air);
            inventorySync.Give(clientId, drop, 1);

            // A door goes whole, both halves for one door.
            if (Blocks.IsDoor(previous)) RemoveDoorHalf(Doors.OtherHalf(pos, previous));

            // A door standing on it falls: whoever broke it gets it back.
            if (Doors.TryFindHeldBy(serverWorld, pos, out BlockPos fallen))
            {
                ushort door = serverWorld.GetBlock(fallen);
                serverWorld.SetBlock(fallen, Blocks.Air);
                RemoveDoorHalf(Doors.OtherHalf(fallen, door));
                inventorySync.Give(clientId, Blocks.Door, 1);
            }

            // The torches it held fall: whoever broke it gets them back, what they can carry.
            heldTorches.Clear();
            Torches.FindHeldBy(serverWorld, pos, heldTorches);
            foreach (BlockPos torch in heldTorches)
            {
                serverWorld.SetBlock(torch, Blocks.Air);
            }

            if (heldTorches.Count > 0) inventorySync.Give(clientId, Blocks.Torch, heldTorches.Count);

            // Next to water, the hole fills up to the water's level.
            if (WaterFill.Find(serverWorld, pos, fill))
            {
                foreach (BlockPos wet in fill)
                {
                    pendingWater.Enqueue(wet);
                }
            }

            EditApplied?.Invoke(clientId, EditKind.Broken);
            return true;
        }

        // Server side: whether a player may break a block on claimed land: the block's column, and that
        // of each torch it holds (which would fall with it). Says whose land it is if not.
        internal bool CanBreakOnClaims(ulong clientId, BlockPos pos)
        {
            if (!claimSync.Check(clientId, pos.X, pos.Z)) return false;

            heldTorches.Clear();
            Torches.FindHeldBy(serverWorld, pos, heldTorches);
            foreach (BlockPos torch in heldTorches)
            {
                if (!claimSync.Check(clientId, torch.X, torch.Z)) return false;
            }

            return true;
        }

        private void RemoveDoorHalf(BlockPos pos)
        {
            if (serverWorld.InBounds(pos.X, pos.Y, pos.Z) && Blocks.IsDoor(serverWorld.GetBlock(pos))) serverWorld.SetBlock(pos, Blocks.Air);
        }

        // Client side: a block the local player's last hit should break, shown broken at once (a door
        // whole); the server sends it back (SendRealBlock) if it disagrees.
        internal void PredictBreak(BlockPos pos)
        {
            if (!ready || serverWorld != null) return;

            World world = voxelWorld.World;
            ushort block = world.GetBlock(pos);
            world.SetBlock(pos, Blocks.Air);
            if (!Blocks.IsDoor(block)) return;

            BlockPos other = Doors.OtherHalf(pos, block);
            if (world.InBounds(other.X, other.Y, other.Z) && Blocks.IsDoor(world.GetBlock(other))) world.SetBlock(other, Blocks.Air);
        }

        // The local player sets up a door with its lower half at pos, facing the way they look: both
        // halves, closed, for one door from the bag. Shown at once on a client, as for any edit.
        public void RequestPlaceDoor(BlockPos pos, int facing)
        {
            if (!ready) return;

            ushort lower = Doors.Make(facing, false, false);
            if (serverWorld != null)
            {
                TryPlaceDoor(networkManager.LocalClientId, pos, lower);
                return;
            }

            voxelWorld.World.SetBlock(pos, lower);
            voxelWorld.World.SetBlock(pos.X, pos.Y + 1, pos.Z, Doors.Make(facing, false, true));
            SendBlock(DoorRequestMessage, NetworkManager.ServerClientId, pos, lower);
        }

        // The local player opens or closes the door one of whose halves is at pos.
        public void RequestToggleDoor(BlockPos pos)
        {
            if (!ready) return;

            if (serverWorld != null)
            {
                TryToggleDoor(networkManager.LocalClientId, pos);
                return;
            }

            World world = voxelWorld.World;
            ushort block = world.GetBlock(pos);
            if (!Blocks.IsDoor(block)) return;

            BlockPos lower = Doors.LowerHalf(pos, block);
            ToggleHalf(world, lower);
            ToggleHalf(world, new BlockPos(lower.X, lower.Y + 1, lower.Z));
            SendBlock(DoorRequestMessage, NetworkManager.ServerClientId, pos, Blocks.Air);
        }

        // The local player builds with a hammer: the block (in its shape) in every free cell of the line
        // from start to end (BuildLines; start and end the same for one), as far as their bag holds it.
        // Shown at once on a client; the server sends back what it refused.
        public void RequestBuild(BlockPos start, BlockPos end, ushort block)
        {
            if (!ready || !Blocks.IsConstruction(block) || Blocks.IsDoor(block)) return;

            if (serverWorld != null)
            {
                Build(networkManager.LocalClientId, start, end, block);
                return;
            }

            BuildLines.Cells(start, end, lineCells);
            int left = inventorySync.Creative ? int.MaxValue : inventorySync.Inventory.Count(Items.OfBlock(block));
            foreach (BlockPos cell in lineCells)
            {
                if (left <= 0) break;
                if (!voxelWorld.World.InBounds(cell.X, cell.Y, cell.Z) || !Blocks.IsReplaceable(voxelWorld.World.GetBlock(cell))) continue;

                voxelWorld.World.SetBlock(cell, block);
                left--;
            }

            SendLine(BuildRequestMessage, start, end, block);
        }

        // The local player takes back with a hammer the construction blocks of the line from start to
        // end (start and end the same for one). Shown at once on a client, as a break.
        public void RequestUnbuild(BlockPos start, BlockPos end)
        {
            if (!ready) return;

            if (serverWorld != null)
            {
                Unbuild(networkManager.LocalClientId, start, end);
                return;
            }

            BuildLines.Cells(start, end, lineCells);
            foreach (BlockPos cell in lineCells)
            {
                if (voxelWorld.World.InBounds(cell.X, cell.Y, cell.Z) && Blocks.IsConstruction(voxelWorld.World.GetBlock(cell))) PredictBreak(cell);
            }

            SendLine(UnbuildRequestMessage, start, end, Blocks.Air);
        }

        private readonly List<BlockPos> lineCells = new List<BlockPos>();
        private readonly Dictionary<ulong, float> lastHammer = new Dictionary<ulong, float>();

        // Server side: whether a player may use their hammer now (one action per HammerInterval).
        private bool MayHammer(ulong clientId)
        {
            float now = Time.unscaledTime;
            if (lastHammer.TryGetValue(clientId, out float last) && now - last < HammerInterval) return false;

            lastHammer[clientId] = now;
            return true;
        }

        // Each cell of the line through TryApplyEdit (reach allowing for the line's length); the cells
        // refused go back to the requester as they really are.
        private void Build(ulong clientId, BlockPos start, BlockPos end, ushort block)
        {
            BuildLines.Cells(start, end, lineCells);
            bool allowed = Blocks.IsConstruction(block) && !Blocks.IsDoor(block) && Blocks.IsValid(block) && MayHammer(clientId);
            foreach (BlockPos cell in lineCells)
            {
                if (allowed && TryApplyEdit(clientId, cell, block, BuildLines.MaxLength)) continue;

                if (clientId != networkManager.LocalClientId && serverWorld.InBounds(cell.X, cell.Y, cell.Z)) SendBlock(BlockChangedMessage, clientId, cell, serverWorld.GetBlock(cell));
            }
        }

        // Each construction block of the line taken back (TryBreak, which wants the hammer they need),
        // at once; other blocks are left. A starving player takes nothing back.
        private void Unbuild(ulong clientId, BlockPos start, BlockPos end)
        {
            BuildLines.Cells(start, end, lineCells);
            bool allowed = !inventorySync.IsStarving(clientId) && MayHammer(clientId);
            foreach (BlockPos cell in lineCells)
            {
                if (!serverWorld.InBounds(cell.X, cell.Y, cell.Z)) continue;

                ushort block = serverWorld.GetBlock(cell);
                if (!Blocks.IsConstruction(block)) continue;
                if (allowed && !storageSync.StandsOn(cell) && TryBreak(clientId, cell, BuildLines.MaxLength))
                {
                    EditApplied?.Invoke(clientId, EditKind.TakenBack);
                    continue;
                }

                SendRealBlock(clientId, cell);
                if (Blocks.IsDoor(block)) SendRealBlock(clientId, Doors.OtherHalf(cell, block));
            }
        }

        private void SendLine(string messageName, BlockPos start, BlockPos end, ushort block)
        {
            using (var writer = new FastBufferWriter(6 * sizeof(int) + sizeof(ushort), Allocator.Temp))
            {
                writer.WriteValueSafe(start.X);
                writer.WriteValueSafe(start.Y);
                writer.WriteValueSafe(start.Z);
                writer.WriteValueSafe(end.X);
                writer.WriteValueSafe(end.Y);
                writer.WriteValueSafe(end.Z);
                writer.WriteValueSafe(block);
                Messaging.SendNamedMessage(messageName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void ReadLine(FastBufferReader reader, out BlockPos start, out BlockPos end, out ushort block)
        {
            reader.ReadValueSafe(out int sx);
            reader.ReadValueSafe(out int sy);
            reader.ReadValueSafe(out int sz);
            reader.ReadValueSafe(out int ex);
            reader.ReadValueSafe(out int ey);
            reader.ReadValueSafe(out int ez);
            reader.ReadValueSafe(out block);
            start = new BlockPos(sx, sy, sz);
            end = new BlockPos(ex, ey, ez);
        }

        private void OnBuildRequest(ulong senderId, FastBufferReader reader)
        {
            ReadLine(reader, out BlockPos start, out BlockPos end, out ushort block);
            Build(senderId, start, end, block);
        }

        private void OnUnbuildRequest(ulong senderId, FastBufferReader reader)
        {
            ReadLine(reader, out BlockPos start, out BlockPos end, out _);
            Unbuild(senderId, start, end);
        }

        private static void ToggleHalf(World world, BlockPos pos)
        {
            ushort block = world.GetBlock(pos);
            if (Blocks.IsDoor(block)) world.SetBlock(pos, Doors.Toggled(block));
        }

        // Within reach, on solid ground with room for both halves, free of storage, players and
        // animals, on land the player may act on, with a door in the bag.
        private bool TryPlaceDoor(ulong clientId, BlockPos pos, ushort lower)
        {
            if (inventorySync.IsStarving(clientId) || !Blocks.IsDoor(lower) || Doors.IsUpper(lower) || Doors.IsOpen(lower)) return false;
            if (!TryGetEyes(clientId, out Vector3 eyes) || !BlockEditRules.IsInReach(eyes.ToNumerics(), pos)) return false;
            if (!Doors.CanStand(serverWorld, pos)) return false;

            var upper = new BlockPos(pos.X, pos.Y + 1, pos.Z);
            if (storageSync != null && (storageSync.IsCovered(pos) || storageSync.IsCovered(upper))) return false;
            if (IsOccupied(pos) || IsOccupied(upper)) return false;
            if (!claimSync.Check(clientId, pos.X, pos.Z)) return false;
            if (!inventorySync.Owns(clientId, Items.Hammer) || !inventorySync.TryConsumePlaced(clientId, Blocks.Door)) return false;

            serverWorld.SetBlock(pos, lower);
            serverWorld.SetBlock(upper, Doors.Make(Doors.FacingOf(lower), false, true));
            EditApplied?.Invoke(clientId, EditKind.Placed);
            return true;
        }

        // Within reach, on land the player may act on; closed only while nobody stands in it.
        private bool TryToggleDoor(ulong clientId, BlockPos pos)
        {
            ushort block = serverWorld.GetBlock(pos);
            if (!Blocks.IsDoor(block)) return false;
            if (!TryGetEyes(clientId, out Vector3 eyes) || !BlockEditRules.IsInReach(eyes.ToNumerics(), pos)) return false;

            BlockPos lower = Doors.LowerHalf(pos, block);
            var upper = new BlockPos(lower.X, lower.Y + 1, lower.Z);
            if (!Blocks.IsDoor(serverWorld.GetBlock(lower)) || !Blocks.IsDoor(serverWorld.GetBlock(upper))) return false;
            if (!claimSync.Check(clientId, lower.X, lower.Z)) return false;
            if (Doors.IsOpen(block) && (IsOccupied(lower) || IsOccupied(upper))) return false;

            serverWorld.SetBlock(lower, Doors.Toggled(serverWorld.GetBlock(lower)));
            serverWorld.SetBlock(upper, Doors.Toggled(serverWorld.GetBlock(upper)));
            return true;
        }

        private void OnDoorRequest(ulong senderId, FastBufferReader reader)
        {
            ReadBlock(reader, out BlockPos pos, out ushort block);
            bool placing = Blocks.IsDoor(block);
            if (placing ? TryPlaceDoor(senderId, pos, block) : TryToggleDoor(senderId, pos)) return;

            // Refusal: the real state of both cells goes back to the requester.
            ushort current = serverWorld.GetBlock(pos);
            BlockPos other = placing ? new BlockPos(pos.X, pos.Y + 1, pos.Z) : Blocks.IsDoor(current) ? Doors.OtherHalf(pos, current) : new BlockPos(pos.X, pos.Y + 1, pos.Z);
            SendBlock(BlockChangedMessage, senderId, pos, current);
            if (serverWorld.InBounds(other.X, other.Y, other.Z)) SendBlock(BlockChangedMessage, senderId, other, serverWorld.GetBlock(other));
        }

        private bool TryGetEyes(ulong clientId, out Vector3 eyes)
        {
            eyes = Vector3.zero;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return false;

            eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            return true;
        }

        private bool IsOccupied(BlockPos pos) => IsInsideAnyPlayer(pos) || ecosystemSync.IsAnimalInside(pos);

        // Server side: tells a client the real state of a block, undoing a break it anticipated.
        internal void SendRealBlock(ulong clientId, BlockPos pos)
        {
            if (serverWorld != null && clientId != networkManager.LocalClientId) SendBlock(BlockChangedMessage, clientId, pos, serverWorld.GetBlock(pos));
        }

        // The local player tills a block with their hoe: grass, dirt, mud or clay becomes tilled soil,
        // the only ground where seeds can be sown. Shown at once on a client, as for any edit.
        public void RequestTill(BlockPos pos)
        {
            if (!ready) return;

            if (serverWorld != null)
            {
                TryTill(networkManager.LocalClientId, pos);
                return;
            }

            voxelWorld.World.SetBlock(pos, Blocks.TilledSoil);
            SendBlock(TillRequestMessage, NetworkManager.ServerClientId, pos, Blocks.TilledSoil);
        }

        // Within reach, with a hoe, on tillable ground with nothing solid on it.
        private bool TryTill(ulong clientId, BlockPos pos)
        {
            if (inventorySync.IsStarving(clientId)) return false;
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            var centre = new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + 0.87f;
            if ((centre - eyes).sqrMagnitude > reach * reach) return false;

            if (!ToolRules.CanTill(serverWorld.GetBlock(pos))) return false;
            if (Blocks.IsSolid(serverWorld.GetBlock(pos.X, pos.Y + 1, pos.Z))) return false;
            if (!inventorySync.Owns(clientId, Items.Hoe)) return false;
            if (!claimSync.Check(clientId, pos.X, pos.Z)) return false;

            serverWorld.SetBlock(pos, Blocks.TilledSoil);
            EditApplied?.Invoke(clientId, EditKind.Tilled);
            return true;
        }

        private void OnTillRequest(ulong senderId, FastBufferReader reader)
        {
            ReadBlock(reader, out BlockPos pos, out _);

            // Refusal: the real block goes back to the requester, as for any edit.
            if (!TryTill(senderId, pos)) SendBlock(BlockChangedMessage, senderId, pos, serverWorld.GetBlock(pos));
        }

        private void Update()
        {
            if (serverWorld == null) return;

            for (int i = 0; i < WaterBlocksPerFrame && pendingWater.Count > 0; i++)
            {
                // A block placed meanwhile keeps its place.
                BlockPos pos = pendingWater.Dequeue();
                if (Blocks.TypeOf(serverWorld.GetBlock(pos)) == Blocks.Air) serverWorld.SetBlock(pos, Blocks.Water);
            }
        }

        private bool IsEditAllowed(ulong clientId, BlockPos pos, ushort block, float extraReach = 0f)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            if (!BlockEditRules.IsAllowed(serverWorld, eyes.ToNumerics(), pos, block, extraReach)) return false;

            // A block is never placed inside storage, a player or an animal.
            if (storageSync != null && storageSync.IsCovered(pos)) return false;

            return !Blocks.IsSolid(block) || (!IsInsideAnyPlayer(pos, block) && !ecosystemSync.IsAnimalInside(pos));
        }

        // Whether a player stands where a block (of that shape, Shapes) set at pos would be.
        private bool IsInsideAnyPlayer(BlockPos pos, ushort block = Blocks.Stone)
        {
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;

                System.Numerics.Vector3 feet = client.PlayerObject.transform.position.ToNumerics();
                if (VoxelCollision.IntersectsBlock(serverWorld, feet, PlayerRules.HalfWidth, PlayerRules.Height, pos, block)) return true;
            }

            return false;
        }

        private void OnSetBlockRequest(ulong senderId, FastBufferReader reader)
        {
            ReadBlock(reader, out BlockPos pos, out ushort block);

            if (!TryApplyEdit(senderId, pos, block))
            {
                // Refusal: the real state of the block is sent back to the requester, which undoes its anticipated edit.
                SendBlock(BlockChangedMessage, senderId, pos, serverWorld.GetBlock(pos));
            }
        }

        private void BroadcastBlockChange(BlockPos pos, ushort previous, ushort current)
        {
            foreach (ulong clientId in networkManager.ConnectedClientsIds)
            {
                if (clientId != networkManager.LocalClientId)
                {
                    SendBlock(BlockChangedMessage, clientId, pos, current);
                }
            }
        }

        private void OnClientConnected(ulong clientId)
        {
            if (clientId == networkManager.LocalClientId) return;

            // The client generates the terrain itself from the seed: only the chunks modified
            // since generation are sent to it.
            Vector3 playerPosition = networkManager.ConnectedClients[clientId].PlayerObject.transform.position;
            var stream = new ChunkStream(serverWorld, playerPosition);

            using (var writer = new FastBufferWriter(WorldInfoSize, Allocator.Temp))
            {
                writer.WriteValueSafe(serverWorld.ChunksX);
                writer.WriteValueSafe(serverWorld.ChunksY);
                writer.WriteValueSafe(serverWorld.ChunksZ);
                writer.WriteValueSafe(serverWorld.Seed);
                writer.WriteValueSafe(serverWorld.GeneratorVersion);
                writer.WriteValueSafe(serverWorld.TerrainFingerprint);
                writer.WriteValueSafe(stream.ChunkCount);
                Messaging.SendNamedMessage(WorldInfoMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }

            if (stream.ChunkCount > 0) streams[clientId] = stream;
        }

        private void OnClientDisconnected(ulong clientId)
        {
            streams.Remove(clientId);
        }

        private void SendChunkBatches()
        {
            if (streams.Count == 0) return;

            foreach (KeyValuePair<ulong, ChunkStream> entry in streams)
            {
                byte[] batch = entry.Value.NextBatch(MaxBatchBytes);
                using (var writer = new FastBufferWriter(batch.Length + sizeof(int), Allocator.Temp))
                {
                    writer.WriteValueSafe(batch.Length);
                    writer.WriteBytesSafe(batch);
                    Messaging.SendNamedMessage(ChunksMessage, entry.Key, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }

                if (entry.Value.IsFinished) finishedStreams.Add(entry.Key);
            }

            foreach (ulong clientId in finishedStreams)
            {
                ChunkStream finished = streams[clientId];
                Debug.Log(
                    $"World sent to player {clientId}: {finished.BytesSent / 1024} KB in {Time.unscaledTime - finished.StartedAt:0.0} s.");
                streams.Remove(clientId);
            }

            finishedStreams.Clear();
        }

        private void OnWorldInfo(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int chunksX);
            reader.ReadValueSafe(out int chunksY);
            reader.ReadValueSafe(out int chunksZ);
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out int generatorVersion);
            reader.ReadValueSafe(out ulong fingerprint);
            reader.ReadValueSafe(out int modifiedChunkCount);

            voxelWorld.CreateRemote(chunksX, chunksY, chunksZ, seed);

            // A terrain different from the server's would give invisible walls and holes: better not
            // to play. This only happens if the two do not run the same version of the game.
            World generated = voxelWorld.World;
            if (generated.GeneratorVersion != generatorVersion || generated.TerrainFingerprint != fingerprint)
            {
                Failure = "This server does not run the same version of the game: the generated world does not match its own.";
                Debug.LogError(Failure);
                networkManager.Shutdown();
                return;
            }

            expectedChunkCount = modifiedChunkCount;
            receivedChunkCount = 0;
            ready = modifiedChunkCount == 0;
        }

        private void OnChunks(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int length);
            var batch = new byte[length];
            reader.ReadBytesSafe(ref batch, length);

            receivedChunks.Clear();
            using (var batchReader = new BinaryReader(new MemoryStream(batch)))
            {
                ChunkBatch.Read(batchReader, voxelWorld.World, receivedChunks);
            }

            foreach (Chunk chunk in receivedChunks)
            {
                voxelWorld.RefreshChunk(chunk);
            }

            receivedChunkCount += receivedChunks.Count;
            if (receivedChunkCount >= expectedChunkCount) ready = true;
        }

        private void OnBlockChanged(ulong senderId, FastBufferReader reader)
        {
            ReadBlock(reader, out BlockPos pos, out ushort block);
            voxelWorld.World.SetBlock(pos, block);
        }

        private void SendBlock(string messageName, ulong clientId, BlockPos pos, ushort block)
        {
            using (var writer = new FastBufferWriter(BlockMessageSize, Allocator.Temp))
            {
                writer.WriteValueSafe(pos.X);
                writer.WriteValueSafe(pos.Y);
                writer.WriteValueSafe(pos.Z);
                writer.WriteValueSafe(block);
                Messaging.SendNamedMessage(messageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void ReadBlock(FastBufferReader reader, out BlockPos pos, out ushort block)
        {
            reader.ReadValueSafe(out int x);
            reader.ReadValueSafe(out int y);
            reader.ReadValueSafe(out int z);
            reader.ReadValueSafe(out block);
            pos = new BlockPos(x, y, z);
        }

        // Chunks left to send to a client, those nearest to its player first.
        private sealed class ChunkStream
        {
            private readonly List<Chunk> chunks;
            private int next;

            public ChunkStream(World world, Vector3 playerPosition)
            {
                chunks = new List<Chunk>();
                foreach (Chunk chunk in world.Chunks)
                {
                    if (chunk.IsModified) chunks.Add(chunk);
                }

                chunks.Sort((a, b) => DistanceSquared(a, playerPosition).CompareTo(DistanceSquared(b, playerPosition)));
                StartedAt = Time.unscaledTime;
            }

            public int ChunkCount => chunks.Count;

            public bool IsFinished => next >= chunks.Count;

            public float StartedAt { get; }

            public long BytesSent { get; private set; }

            // Chunks are serialized when sent: a block changed in the meantime is therefore included.
            public byte[] NextBatch(int maxBytes)
            {
                var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream))
                {
                    next += ChunkBatch.Write(writer, chunks, next, maxBytes);
                }

                byte[] batch = stream.ToArray();
                BytesSent += batch.Length;
                return batch;
            }

            private static float DistanceSquared(Chunk chunk, Vector3 position)
            {
                var centre = new Vector3(chunk.Coord.X + 0.5f, chunk.Coord.Y + 0.5f, chunk.Coord.Z + 0.5f) * Chunk.Size;
                return (centre - position).sqrMagnitude;
            }
        }
    }
}
