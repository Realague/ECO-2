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
        private string savePath;
        private bool rendering;

        public World World { get; private set; }

        private void Awake()
        {
            // Un serveur sans affichage détient le monde mais ne le maille pas.
            rendering = !Application.isBatchMode;
            if (!rendering) return;

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

        // Monde du serveur : lu depuis la sauvegarde du dossier donné, ou généré s'il n'y en a pas,
        // et réécrit par Save et Unload. Une taille en chunks peut remplacer celle réglée dans la scène.
        public void LoadPersistent(string directory, Vector3Int? size = null)
        {
            if (size.HasValue) sizeInChunks = size.Value;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            string path = Path.Combine(directory, saveFileName);
            Show(LoadOrCreateWorld(path), path);
            Debug.Log(
                $"Monde de {World.SizeX} × {World.SizeY} × {World.SizeZ} blocs ({World.Chunks.Count} chunks) prêt en {timer.Elapsed.TotalSeconds:0.0} s.");
        }

        // Monde d'un client : vide au départ, rempli par les chunks que le serveur envoie, jamais sauvegardé.
        public void CreateRemote(int chunksX, int chunksY, int chunksZ)
        {
            Show(new World(chunksX, chunksY, chunksZ), null);
        }

        // Écrit le monde sur le disque s'il est persistant. Un échec est signalé sans interrompre la partie.
        public void Save()
        {
            if (World == null || savePath == null) return;

            try
            {
                WorldSave.SaveToFile(savePath, World);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Échec de la sauvegarde du monde : {e.Message} ({savePath})");
            }
        }

        // À appeler quand les blocs du monde ont été remplacés en masse, sans passer par World.SetBlock :
        // un client le fait une fois tous les chunks reçus. Mailler chunk par chunk pendant la réception
        // referait plusieurs fois chaque maillage, voisins compris, et le client ne suivrait plus le flux.
        public void RemeshAll()
        {
            if (!rendering) return;

            foreach (Chunk chunk in World.Chunks)
            {
                dirtyChunks.Add(chunk);
            }
        }

        public void Unload()
        {
            if (World == null) return;

            World.BlockChanged -= OnBlockChanged;
            Save();

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
            savePath = null;
        }

        // Un chemin de sauvegarde nul désigne un monde qui n'est jamais écrit sur le disque.
        private void Show(World world, string path)
        {
            Unload();

            savePath = path;
            World = world;
            if (!rendering) return;

            World.BlockChanged += OnBlockChanged;
            foreach (Chunk chunk in World.Chunks)
            {
                CreateChunkObject(chunk);
            }
        }

        private World LoadOrCreateWorld(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    World saved = WorldSave.LoadFromFile(path, new ProvisionalHillsGenerator());
                    if (MatchesSettings(saved)) return saved;

                    Debug.LogWarning(
                        $"Sauvegarde ignorée : sa graine ou ses dimensions ne correspondent pas aux réglages du monde. " +
                        $"Elle sera remplacée à la prochaine sauvegarde. ({path})");
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Sauvegarde illisible, un nouveau monde est généré et la remplacera à la prochaine sauvegarde : {e.Message} ({path})");
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
