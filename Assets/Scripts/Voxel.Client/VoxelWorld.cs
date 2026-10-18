using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Holds the world of a game, displays it and remeshes the chunks that change. The world only
    // exists between LoadPersistent or CreateRemote and Unload.
    public class VoxelWorld : MonoBehaviour
    {
        private static readonly BlockPos[] NeighbourOffsets =
        {
            new BlockPos(1, 0, 0), new BlockPos(-1, 0, 0),
            new BlockPos(0, 1, 0), new BlockPos(0, -1, 0),
            new BlockPos(0, 0, 1), new BlockPos(0, 0, -1)
        };

        // In chunks: 64 × 8 × 64 is 1024 × 128 × 1024 blocks.
        [SerializeField] private Vector3Int sizeInChunks = new Vector3Int(64, 8, 64);
        [SerializeField] private int seed = 1;
        [SerializeField] private Material blockMaterial;

        // Transparent, drawn on each chunk's second submesh (ChunkMesher.WaterSubmesh).
        [SerializeField] private Material waterMaterial;
        [SerializeField] private string saveFileName = "world.dat";

        [Tooltip("Brightness kept where no sky light reaches, deep underground (1 is open sky).")]
        [SerializeField, Range(0f, 0.2f)] private float darkestSkyLight = 0.02f;

        private readonly Dictionary<Chunk, Mesh> meshes = new Dictionary<Chunk, Mesh>();
        private readonly List<GameObject> chunkObjects = new List<GameObject>();
        private readonly HashSet<Chunk> dirtyChunks = new HashSet<Chunk>();
        private Material materialInstance;
        private Texture2D atlas;
        private SkyLight skyLight;
        private string savePath;
        private bool rendering;

        public World World { get; private set; }

        private void Awake()
        {
            // A server without a display holds the world but does not mesh it.
            rendering = !Application.isBatchMode;
            if (!rendering) return;

            atlas = BlockAtlas.CreateTexture();
            materialInstance = new Material(blockMaterial) { mainTexture = atlas };
            Shader.SetGlobalFloat("_DarkestSkyLight", darkestSkyLight);
        }

        // How bright the blocks are drawn at this position for the sky they see, 0 to 1: the curve of
        // the blocks' shader (SkyBrightness), for what is drawn with other shaders. 1 without a world.
        public float SkyBrightnessAt(Vector3 position)
        {
            if (skyLight == null) return 1f;

            float level = skyLight.Get(Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.y), Mathf.FloorToInt(position.z))
                / (float)SkyLight.Max;
            float curve = level / (3f * (1f - level) + 1f);
            return Mathf.Lerp(darkestSkyLight, 1f, curve);
        }

        private void LateUpdate()
        {
            if (dirtyChunks.Count == 0) return;

            foreach (Chunk chunk in dirtyChunks)
            {
                ChunkMesher.Build(World, skyLight, chunk, meshes[chunk]);
            }

            dirtyChunks.Clear();
        }

        private void OnDestroy()
        {
            Unload();
            Destroy(materialInstance);
            Destroy(atlas);
        }

        // Server world: read from the save in the given folder, or generated if there is none,
        // and written back by Save and Unload. A size in chunks can replace the one set in the scene.
        public void LoadPersistent(string directory, Vector3Int? size = null)
        {
            if (size.HasValue) sizeInChunks = size.Value;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            string path = Path.Combine(directory, saveFileName);
            Show(LoadOrCreateWorld(path), path);
            Debug.Log(
                $"World of {World.SizeX} × {World.SizeY} × {World.SizeZ} blocks ({World.Chunks.Count} chunks) ready in {timer.Elapsed.TotalSeconds:0.0} s.");
        }

        // Client world: generated here from the server's seed, then completed by the modified chunks
        // the server sends. It is never saved.
        public void CreateRemote(int chunksX, int chunksY, int chunksZ, int serverSeed)
        {
            var world = new World(chunksX, chunksY, chunksZ);
            world.Generate(new TerrainGenerator(), serverSeed);
            Show(world, null);
        }

        // Writes the world to disk if it is persistent. A failure is reported without interrupting the game.
        public void Save()
        {
            if (World == null || savePath == null) return;

            try
            {
                WorldSave.SaveToFile(savePath, World);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogError($"Failed to save the world: {e.Message} ({savePath})");
            }
        }

        // To call when the blocks of a chunk were replaced wholesale, without going through World.SetBlock:
        // a client does so for each modified chunk received from the server.
        public void RefreshChunk(Chunk chunk)
        {
            if (!rendering) return;

            dirtyChunks.Add(chunk);
            skyLight.UpdateChunk(chunk, dirtyChunks);

            // The faces of neighbouring chunks turned toward this one change too.
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
            skyLight = null;
            World = null;
            savePath = null;
        }

        // A null save path marks a world that is never written to disk.
        private void Show(World world, string path)
        {
            Unload();

            savePath = path;
            World = world;
            if (!rendering) return;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            skyLight = new SkyLight(World);
            double lightSeconds = timer.Elapsed.TotalSeconds;

            World.BlockChanged += OnBlockChanged;
            foreach (Chunk chunk in World.Chunks)
            {
                CreateChunkObject(chunk);
            }

            Debug.Log($"Sky light worked out in {lightSeconds * 1000:0} ms, chunks meshed in {(timer.Elapsed.TotalSeconds - lightSeconds) * 1000:0} ms.");
        }

        private World LoadOrCreateWorld(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    World saved = WorldSave.LoadFromFile(path, new TerrainGenerator());
                    if (MatchesSettings(saved)) return saved;

                    Debug.LogWarning(
                        $"Save ignored: its seed or dimensions do not match the world's settings. " +
                        $"It will be replaced at the next save. ({path})");
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Unreadable save, a new world is generated and will replace it at the next save: {e.Message} ({path})");
                }
            }

            return CreateWorld();
        }

        private World CreateWorld()
        {
            var world = new World(sizeInChunks.x, sizeInChunks.y, sizeInChunks.z);
            world.Generate(new TerrainGenerator(), seed);
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
            ChunkMesher.Build(World, skyLight, chunk, mesh);
            meshes.Add(chunk, mesh);

            var chunkObject = new GameObject(mesh.name);
            chunkObject.transform.SetParent(transform, false);
            chunkObject.transform.localPosition = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * Chunk.Size;
            chunkObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            chunkObject.AddComponent<MeshRenderer>().sharedMaterials = new[] { materialInstance, waterMaterial };
            chunkObjects.Add(chunkObject);
        }

        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            MarkDirty(pos);
            skyLight.Update(pos, dirtyChunks);

            // A block on a chunk edge also changes the visible faces of the neighbouring chunk.
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
