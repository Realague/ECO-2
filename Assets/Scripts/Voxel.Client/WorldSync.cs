using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Synchronises the world between the server and the clients with named messages.
    // Server: sends the modified chunks to each joining client, validates block edit requests
    // and broadcasts every change. Client: rebuilds the received world and applies the changes.
    public class WorldSync : MonoBehaviour
    {
        private const string WorldInfoMessage = "World.Info";
        private const string ChunksMessage = "World.Chunks";
        private const string SetBlockRequestMessage = "World.SetBlockRequest";
        private const string TillRequestMessage = "World.Till";
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

        private readonly Dictionary<ulong, ChunkStream> streams = new Dictionary<ulong, ChunkStream>();
        private readonly List<ulong> finishedStreams = new List<ulong>();
        private readonly List<Chunk> receivedChunks = new List<Chunk>();

        // Server side: blocks water is pouring into (WaterFill), taken a few at a time so that a
        // hole fills up visibly and the changes do not all go out on the same tick.
        private readonly Queue<BlockPos> pendingWater = new Queue<BlockPos>();
        private readonly List<BlockPos> fill = new List<BlockPos>();
        private World serverWorld;
        private bool running;
        private bool ready;
        private int expectedChunkCount;
        private int receivedChunkCount;

        // Server side: a player's edit was accepted (their client id, and true if they broke a block).
        public event Action<ulong, bool> EditApplied;

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

        // Edit requested by the local player. The server decides; a client applies it right away
        // for responsiveness, and the server corrects it if it refuses.
        public void RequestSetBlock(BlockPos pos, ushort block)
        {
            if (!ready) return;

            if (serverWorld != null)
            {
                TryApplyEdit(networkManager.LocalClientId, pos, block);
                return;
            }

            voxelWorld.World.SetBlock(pos, block);
            SendBlock(SetBlockRequestMessage, NetworkManager.ServerClientId, pos, block);
        }

        private bool TryApplyEdit(ulong clientId, BlockPos pos, ushort block)
        {
            if (!IsEditAllowed(clientId, pos, block)) return false;

            // Placing a block takes it from the player's inventory; breaking one gives it to them, so
            // a player who cannot carry it cannot break it.
            bool placing = Blocks.IsSolid(block);
            ushort previous = serverWorld.GetBlock(pos);

            // Breaking needs the right tool. The server cannot know which one is in hand (the hotbar's
            // choice is the client's): the player must at least own it.
            if (!placing && !inventorySync.Owns(clientId, ToolRules.ToBreak(previous))) return false;

            if (!placing && inventorySync.RoomFor(clientId, Items.DropOf(previous)) <= 0)
            {
                inventorySync.TellFull(clientId, InventorySync.ReasonFor(Items.DropOf(previous)));
                return false;
            }

            if (placing && !inventorySync.TryConsume(clientId, Items.OfBlock(block))) return false;

            serverWorld.SetBlock(pos, block);
            if (!placing)
            {
                inventorySync.Give(clientId, Items.DropOf(previous), 1);

                // Next to water, the hole fills up to the water's level.
                if (WaterFill.Find(serverWorld, pos, fill))
                {
                    foreach (BlockPos wet in fill)
                    {
                        pendingWater.Enqueue(wet);
                    }
                }
            }

            EditApplied?.Invoke(clientId, !placing);
            return true;
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
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client) || client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            var centre = new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
            float reach = PlayerRules.Reach + BlockEditRules.ReachTolerance + 0.87f;
            if ((centre - eyes).sqrMagnitude > reach * reach) return false;

            if (!ToolRules.CanTill(serverWorld.GetBlock(pos))) return false;
            if (Blocks.IsSolid(serverWorld.GetBlock(pos.X, pos.Y + 1, pos.Z))) return false;
            if (!inventorySync.Owns(clientId, Items.Hoe)) return false;

            serverWorld.SetBlock(pos, Blocks.TilledSoil);
            EditApplied?.Invoke(clientId, true);
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

        private bool IsEditAllowed(ulong clientId, BlockPos pos, ushort block)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            if (!BlockEditRules.IsAllowed(serverWorld, eyes.ToNumerics(), pos, block)) return false;

            // A block is never placed inside a player or an animal.
            return !Blocks.IsSolid(block) || (!IsInsideAnyPlayer(pos) && !ecosystemSync.IsAnimalInside(pos));
        }

        private bool IsInsideAnyPlayer(BlockPos pos)
        {
            foreach (NetworkClient client in networkManager.ConnectedClientsList)
            {
                if (client.PlayerObject == null) continue;

                System.Numerics.Vector3 feet = client.PlayerObject.transform.position.ToNumerics();
                if (VoxelCollision.IntersectsBlock(feet, PlayerRules.HalfWidth, PlayerRules.Height, pos)) return true;
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
