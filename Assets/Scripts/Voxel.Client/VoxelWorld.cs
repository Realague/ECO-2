using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Détient le monde d'une partie, l'affiche et remaille les chunks qui changent. Le monde n'existe
    // qu'entre LoadPersistent ou CreateRemote et Unload.
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
        private readonly List<GameObject> chunkObjects = new List<GameObject>();
        private readonly HashSet<Chunk> dirtyChunks = new HashSet<Chunk>();
        private Material materialInstance;
        private Texture2D atlas;
        private bool persistent;

        public World World { get; private set; }

        private string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

        private void Awake()
        {
            atlas = BlockAtlas.CreateTexture();
            materialInstance = new Material(blockMaterial) { mainTexture = atlas };
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
            Unload();
            Destroy(materialInstance);
            Destroy(atlas);
        }

        // Monde du serveur : lu depuis la sauvegarde, ou généré s'il n'y en a pas, et réécrit à Unload.
        public void LoadPersistent()
        {
            Show(LoadOrCreateWorld(), true);
        }

        // Monde d'un client : vide au départ, rempli par les chunks que le serveur envoie, jamais sauvegardé.
        public void CreateRemote(int chunksX, int chunksY, int chunksZ)
        {
            Show(new World(chunksX, chunksY, chunksZ), false);
        }

        // À appeler quand les blocs d'un chunk ont été remplacés en bloc, sans passer par World.SetBlock.
        public void RefreshChunk(Chunk chunk)
        {
            dirtyChunks.Add(chunk);

            // Les faces des chunks voisins tournées vers celui-ci changent aussi.
            foreach (BlockPos offset in NeighbourOffsets)
            {
                var coord = new ChunkCoord(chunk.Coord.X + offset.X, chunk.Coord.Y + offset.Y, chunk.Coord.Z + offset.Z);
                Chunk neighbour = World.GetChunk(coord);
                if (neighbour != null) dirtyChunks.Add(neighbour);
            }
        }

        public void Unload()
        {
            if (World == null) return;

            World.BlockChanged -= OnBlockChanged;
            if (persistent) SaveWorld();

            foreach (GameObject chunkObject in chunkObjects)
            {
                Destroy(chunkObject);
            }

            foreach (Mesh mesh in meshes.Values)
            {
                Destroy(mesh);
            }

            chunkObjects.Clear();
            meshes.Clear();
            dirtyChunks.Clear();
            World = null;
        }

        private void Show(World world, bool isPersistent)
        {
            Unload();

            persistent = isPersistent;
            World = world;
            World.BlockChanged += OnBlockChanged;

            foreach (Chunk chunk in World.Chunks)
            {
                CreateChunkObject(chunk);
            }
        }

        private World LoadOrCreateWorld()
        {
            if (File.Exists(SavePath))
            {
                try
                {
                    World saved = WorldSave.LoadFromFile(SavePath, new ProvisionalHillsGenerator());
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

            return CreateWorld();
        }

        private World CreateWorld()
        {
            var world = new World(sizeInChunks.x, sizeInChunks.y, sizeInChunks.z);
            world.Generate(new ProvisionalHillsGenerator(), seed);
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
            chunkObjects.Add(chunkObject);
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
