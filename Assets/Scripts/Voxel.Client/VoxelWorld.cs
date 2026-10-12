using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Charge ou crée le monde, l'affiche, remaille les chunks touchés par un changement de bloc,
    // et sauvegarde le monde à la fermeture.
    public class VoxelWorld : MonoBehaviour
    {
        private static readonly BlockPos[] NeighbourOffsets =
        {
            new BlockPos(1, 0, 0), new BlockPos(-1, 0, 0),
            new BlockPos(0, 1, 0), new BlockPos(0, -1, 0),
            new BlockPos(0, 0, 1), new BlockPos(0, 0, -1)
        };

        [SerializeField] private Vector3Int sizeInChunks = new Vector3Int(8, 4, 8);
        [SerializeField] private int seed = 1;
        [SerializeField] private Material blockMaterial;
        [SerializeField] private string saveFileName = "world.dat";

        private readonly Dictionary<Chunk, Mesh> meshes = new Dictionary<Chunk, Mesh>();
        private readonly HashSet<Chunk> dirtyChunks = new HashSet<Chunk>();
        private Material materialInstance;
        private Texture2D atlas;

        public World World { get; private set; }

        private string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

        private void Awake()
        {
            World = LoadOrCreateWorld();
            World.BlockChanged += OnBlockChanged;

            atlas = BlockAtlas.CreateTexture();
            materialInstance = new Material(blockMaterial) { mainTexture = atlas };

            foreach (Chunk chunk in World.Chunks)
            {
                CreateChunkObject(chunk);
            }
        }

        private void LateUpdate()
        {
            if (dirtyChunks.Count == 0) return;

            foreach (Chunk chunk in dirtyChunks)
            {
                ChunkMesher.Build(World, chunk, meshes[chunk]);
            }

            dirtyChunks.Clear();
        }

        private void OnDestroy()
        {
            if (World != null)
            {
                World.BlockChanged -= OnBlockChanged;
                SaveWorld();
            }

            foreach (Mesh mesh in meshes.Values)
            {
                Destroy(mesh);
            }

            Destroy(materialInstance);
            Destroy(atlas);
        }

        private World LoadOrCreateWorld()
        {
            var generator = new ProvisionalHillsGenerator();

            if (File.Exists(SavePath))
            {
                try
                {
                    World saved = WorldSave.LoadFromFile(SavePath, generator);
                    if (MatchesSettings(saved)) return saved;

                    Debug.LogWarning(
                        $"Sauvegarde ignorée : sa graine ou ses dimensions ne correspondent pas aux réglages du monde. " +
                        $"Elle sera remplacée à la fermeture. ({SavePath})");
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Sauvegarde illisible, un nouveau monde est généré et la remplacera à la fermeture : {e.Message} ({SavePath})");
                }
            }

            var world = new World(sizeInChunks.x, sizeInChunks.y, sizeInChunks.z);
            world.Generate(generator, seed);
            return world;
        }

        private bool MatchesSettings(World world)
        {
            return world.Seed == seed
                && world.ChunksX == sizeInChunks.x
                && world.ChunksY == sizeInChunks.y
                && world.ChunksZ == sizeInChunks.z;
        }

        private void SaveWorld()
        {
            try
            {
                WorldSave.SaveToFile(SavePath, World);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Échec de la sauvegarde du monde : {e.Message} ({SavePath})");
            }
        }

        private void CreateChunkObject(Chunk chunk)
        {
            var mesh = new Mesh { name = $"Chunk {chunk.Coord}" };
            ChunkMesher.Build(World, chunk, mesh);
            meshes.Add(chunk, mesh);

            var chunkObject = new GameObject(mesh.name);
            chunkObject.transform.SetParent(transform, false);
            chunkObject.transform.localPosition = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * Chunk.Size;
            chunkObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            chunkObject.AddComponent<MeshRenderer>().sharedMaterial = materialInstance;
        }

        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            MarkDirty(pos);

            // Un bloc en bord de chunk change aussi les faces visibles du chunk voisin.
            foreach (BlockPos offset in NeighbourOffsets)
            {
                MarkDirty(pos + offset);
            }
        }

        private void MarkDirty(BlockPos pos)
        {
            Chunk chunk = World.GetChunk(ChunkCoord.FromBlock(pos));
            if (chunk != null) dirtyChunks.Add(chunk);
        }
    }
}
