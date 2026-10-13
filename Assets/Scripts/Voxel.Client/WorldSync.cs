using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Synchronise le monde entre le serveur et les clients par messages nommés.
    // Serveur : envoie tous les chunks à chaque client qui arrive, valide les demandes de modification
    // de bloc et diffuse chaque changement. Client : reconstruit le monde reçu et applique les changements.
    public class WorldSync : MonoBehaviour
    {
        private const string WorldInfoMessage = "World.Info";
        private const string ChunksMessage = "World.Chunks";
        private const string SetBlockRequestMessage = "World.SetBlockRequest";
        private const string BlockChangedMessage = "World.BlockChanged";

        // Un lot de chunks par client et par tick réseau, pour ne pas saturer la connexion.
        private const int MaxBatchBytes = 16 * 1024;
        private const int BlockMessageSize = 3 * sizeof(int) + sizeof(ushort);

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private VoxelWorld voxelWorld;

        private readonly Dictionary<ulong, ChunkStream> streams = new Dictionary<ulong, ChunkStream>();
        private readonly List<ulong> finishedStreams = new List<ulong>();
        private readonly List<Chunk> receivedChunks = new List<Chunk>();
        private World serverWorld;
        private bool running;
        private bool ready;
        private int expectedChunkCount;
        private int receivedChunkCount;

        // Le monde de la partie, ou null tant qu'il n'est pas entièrement disponible sur cette machine.
        public World World => ready ? voxelWorld.World : null;

        public float LoadingProgress => expectedChunkCount > 0 ? (float)receivedChunkCount / expectedChunkCount : 0f;

        private CustomMessagingManager Messaging => networkManager.CustomMessagingManager;

        // À appeler juste après le démarrage du serveur ou de l'hôte, une fois son monde chargé.
        public void StartServer()
        {
            running = true;
            ready = true;
            serverWorld = voxelWorld.World;
            serverWorld.BlockChanged += BroadcastBlockChange;

            Messaging.RegisterNamedMessageHandler(SetBlockRequestMessage, OnSetBlockRequest);
            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            networkManager.NetworkTickSystem.Tick += SendChunkBatches;
        }

        // À appeler juste après le lancement de la connexion d'un client.
        public void StartClient()
        {
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
            serverWorld = null;
            running = false;
            ready = false;
            expectedChunkCount = 0;
            receivedChunkCount = 0;
        }

        // Modification demandée par le joueur local. Le serveur décide ; un client l'applique tout de
        // suite pour la réactivité, et le serveur la corrige s'il la refuse.
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

            serverWorld.SetBlock(pos, block);
            return true;
        }

        private bool IsEditAllowed(ulong clientId, BlockPos pos, ushort block)
        {
            if (!networkManager.ConnectedClients.TryGetValue(clientId, out NetworkClient client)) return false;
            if (client.PlayerObject == null) return false;

            Vector3 eyes = client.PlayerObject.transform.position + Vector3.up * PlayerRules.EyeHeight;
            if (!BlockEditRules.IsAllowed(serverWorld, eyes.ToNumerics(), pos, block)) return false;

            return !Blocks.IsSolid(block) || !IsInsideAnyPlayer(pos);
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
                // Refus : on renvoie au demandeur l'état réel du bloc, qui annule son application anticipée.
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

            using (var writer = new FastBufferWriter(3 * sizeof(int), Allocator.Temp))
            {
                writer.WriteValueSafe(serverWorld.ChunksX);
                writer.WriteValueSafe(serverWorld.ChunksY);
                writer.WriteValueSafe(serverWorld.ChunksZ);
                Messaging.SendNamedMessage(WorldInfoMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
            }

            Vector3 playerPosition = networkManager.ConnectedClients[clientId].PlayerObject.transform.position;
            streams[clientId] = new ChunkStream(serverWorld, playerPosition);
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
                    $"Monde envoyé au joueur {clientId} : {finished.BytesSent / 1024} Ko en {Time.unscaledTime - finished.StartedAt:0.0} s.");
                streams.Remove(clientId);
            }

            finishedStreams.Clear();
        }

        private void OnWorldInfo(ulong senderId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out int chunksX);
            reader.ReadValueSafe(out int chunksY);
            reader.ReadValueSafe(out int chunksZ);

            voxelWorld.CreateRemote(chunksX, chunksY, chunksZ);
            expectedChunkCount = voxelWorld.World.Chunks.Count;
            receivedChunkCount = 0;
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

            receivedChunkCount += receivedChunks.Count;
            if (receivedChunkCount >= expectedChunkCount && !ready)
            {
                voxelWorld.RemeshAll();
                ready = true;
            }
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

        // Chunks restant à envoyer à un client, les plus proches de son joueur d'abord.
        private sealed class ChunkStream
        {
            private readonly List<Chunk> chunks;
            private int next;

            public ChunkStream(World world, Vector3 playerPosition)
            {
                chunks = new List<Chunk>(world.Chunks);
                chunks.Sort((a, b) => DistanceSquared(a, playerPosition).CompareTo(DistanceSquared(b, playerPosition)));
                StartedAt = Time.unscaledTime;
            }

            public bool IsFinished => next >= chunks.Count;

            public float StartedAt { get; }

            public long BytesSent { get; private set; }

            // Les chunks sont sérialisés au moment de l'envoi : un bloc modifié entre-temps est donc inclus.
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
