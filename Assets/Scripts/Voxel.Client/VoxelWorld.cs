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

        // Torches (TorchViews): the stick, its head and the flame, all with GPU instancing.
        [SerializeField] private Material torchWoodMaterial;
        [SerializeField] private Material torchHeadMaterial;
        [SerializeField] private Material torchFlameMaterial;

        // Remeshing this many chunks or more at once is shared among the cores (ChunkMeshing).
        private const int ParallelRemeshCount = 16;

        // Only chunks with faces to draw have an object, with its mesh (most chunks are air or buried
        // rock); a chunk that gains faces gets one when it is remeshed.
        private readonly Dictionary<Chunk, MeshFilter> chunkMeshes = new Dictionary<Chunk, MeshFilter>();
        private readonly HashSet<Chunk> dirtyChunks = new HashSet<Chunk>();
        private readonly List<Chunk> remeshing = new List<Chunk>();
        private ChunkMeshBuilder builder;
        private Material materialInstance;
        private Texture2D atlas;
        private SkyLight skyLight;
        private BlockLight blockLight;
        private TorchViews torchViews;
        private string savePath;
        private bool rendering;

        public World World { get; private set; }

        // The blocks' material with the atlas, for what is drawn in block tiles (stockpiles); null on a
        // server without a display.
        public Material BlockMaterial => materialInstance;

        private void Awake()
        {
            // A server without a display holds the world but does not mesh it.
            rendering = !Application.isBatchMode;
            if (!rendering) return;

            atlas = BlockAtlas.CreateTexture();
            materialInstance = new Material(blockMaterial) { mainTexture = atlas };
            Shader.SetGlobalFloat("_DarkestSkyLight", darkestSkyLight);
            if (torchWoodMaterial != null && torchHeadMaterial != null && torchFlameMaterial != null)
            {
                torchViews = new TorchViews(torchWoodMaterial, torchHeadMaterial, torchFlameMaterial);
            }
        }

        // How bright the blocks are drawn at this position, 0 to 1: the curve of the blocks' shader
        // for the sky they see (SkyBrightness), or for a torch's light near one (TorchGlow) if that
        // is brighter; for what is drawn with other shaders. 1 without a world.
        public float SkyBrightnessAt(Vector3 position)
        {
            if (skyLight == null) return 1f;

            int x = Mathf.FloorToInt(position.x);
            int y = Mathf.FloorToInt(position.y);
            int z = Mathf.FloorToInt(position.z);
            float level = skyLight.Get(x, y, z) / (float)SkyLight.Max;
            float curve = level / (3f * (1f - level) + 1f);
            float sky = Mathf.Lerp(darkestSkyLight, 1f, curve);
            float torch = blockLight != null ? TorchGlow(blockLight.Get(x, y, z) / (float)Blocks.TorchLight) : 0f;
            return Mathf.Max(sky, torch);
        }

        // The blocks' shader's curve for a torch's light (0 to 1 at Blocks.TorchLight): bright by the
        // torch, falling by about a fifth per block.
        private static float TorchGlow(float level) => level <= 0f ? 0f : (Mathf.Pow(0.8f, (1f - level) * Blocks.TorchLight) - 0.044f) / 0.956f;

        private void LateUpdate()
        {
            if (World != null && torchViews != null && Camera.main != null) torchViews.Render(World, blockLight, Camera.main.transform.position);
            if (dirtyChunks.Count == 0) return;

            if (dirtyChunks.Count >= ParallelRemeshCount)
            {
                remeshing.Clear();
                remeshing.AddRange(dirtyChunks);
                ChunkMeshing.BuildAll(World, skyLight, remeshing, ShowMesh, blockLight);
                remeshing.Clear();
            }
            else
            {
                builder ??= new ChunkMeshBuilder();
                foreach (Chunk chunk in dirtyChunks)
                {
                    builder.Build(World, skyLight, chunk, blockLight);
                    ShowMesh(chunk, builder);
                }
            }

            dirtyChunks.Clear();
        }

        private void OnDestroy()
        {
            Unload();
            Destroy(materialInstance);
            Destroy(atlas);
        }

        // Server world: read from the save in the given folder, with its own seed and size, or
        // generated if there is none, and written back by Save and Unload. A new world takes the
        // given size in chunks and seed, or those set in the scene, and is saved at once, so that
        // the list of worlds knows it.
        public void LoadPersistent(string directory, Vector3Int? size = null, int? newSeed = null)
        {
            if (size.HasValue) sizeInChunks = size.Value;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            string path = Path.Combine(directory, saveFileName);
            Show(LoadOrCreateWorld(path, newSeed ?? seed, out bool created), path);
            if (created) Save();
            Debug.Log(
                $"World of {World.SizeX} × {World.SizeY} × {World.SizeZ} blocks ({World.Chunks.Count} chunks), seed {World.Seed}, "
                + $"{(created ? "created" : "loaded")} in {timer.Elapsed.TotalSeconds:0.0} s.");
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
            blockLight.UpdateChunk(chunk, dirtyChunks);

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

            foreach (MeshFilter filter in chunkMeshes.Values)
            {
                Destroy(filter.sharedMesh);
                Destroy(filter.gameObject);
            }

            chunkMeshes.Clear();
            dirtyChunks.Clear();
            skyLight = null;
            blockLight = null;
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
            blockLight = new BlockLight(World);
            double lightSeconds = timer.Elapsed.TotalSeconds;

            World.BlockChanged += OnBlockChanged;
            ChunkMeshing.BuildAll(World, skyLight, World.Chunks, ShowMesh, blockLight);

            Debug.Log(
                $"Sky light worked out in {lightSeconds * 1000:0} ms, chunks meshed in {(timer.Elapsed.TotalSeconds - lightSeconds) * 1000:0} ms "
                + $"({chunkMeshes.Count} of {World.Chunks.Count} have faces).");
        }

        // A saved world keeps its own seed and size; only a new one takes those asked for.
        private World LoadOrCreateWorld(string path, int worldSeed, out bool created)
        {
            created = false;
            if (File.Exists(path))
            {
                try
                {
                    return WorldSave.LoadFromFile(path, new TerrainGenerator());
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException)
                {
                    Debug.LogWarning(
                        $"Unreadable save, a new world is generated and will replace it at the next save: {e.Message} ({path})");
                }
            }

            created = true;
            var world = new World(sizeInChunks.x, sizeInChunks.y, sizeInChunks.z);
            world.Generate(new TerrainGenerator(), worldSeed);
            return world;
        }

        // Puts a built mesh on screen, in a new Mesh: the chunk's object takes it in place of its old
        // one, or a chunk without an object gets one if it now has faces (a chunk left without faces
        // keeps its object, with an empty mesh). Meshes keep no copy of their data on the CPU
        // (UploadMeshData), hundreds of megabytes for a whole world, so they cannot be written again:
        // remeshing replaces them. Nothing reads them back (collisions and rays use the world).
        private void ShowMesh(Chunk chunk, ChunkMeshBuilder built)
        {
            bool hasObject = chunkMeshes.TryGetValue(chunk, out MeshFilter filter);
            if (!hasObject && built.IsEmpty) return;

            var mesh = new Mesh { name = $"Chunk {chunk.Coord}" };
            built.ApplyTo(mesh);
            mesh.UploadMeshData(true);

            if (hasObject)
            {
                Destroy(filter.sharedMesh);
                filter.sharedMesh = mesh;
                return;
            }

            var chunkObject = new GameObject(mesh.name);
            chunkObject.transform.SetParent(transform, false);
            chunkObject.transform.localPosition = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * Chunk.Size;
            filter = chunkObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            chunkObject.AddComponent<MeshRenderer>().sharedMaterials = new[] { materialInstance, waterMaterial };
            chunkMeshes.Add(chunk, filter);
        }

        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            MarkDirty(pos);
            skyLight.Update(pos, dirtyChunks);
            blockLight.Update(pos, dirtyChunks);

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
