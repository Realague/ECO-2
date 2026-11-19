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

        // The smoothed ground, drawn on the third submesh (ChunkMesher.SmoothSubmesh): Terrain.mat, the
        // blocks' shader with _ECO_SMOOTH on. Without it, the ground is drawn in blocks.
        [SerializeField] private Material terrainMaterial;
        [SerializeField] private string saveFileName = "world.dat";

        [Tooltip("Brightness kept where no sky light reaches, deep underground (1 is open sky).")]
        [SerializeField, Range(0f, 0.2f)] private float darkestSkyLight = 0.02f;

        // Torches (TorchViews): the stick, its head and the flame, all with GPU instancing.
        [SerializeField] private Material torchWoodMaterial;
        [SerializeField] private Material torchHeadMaterial;
        [SerializeField] private Material torchFlameMaterial;

        // Doors (DoorViews): planks and iron, with GPU instancing.
        [SerializeField] private Material doorWoodMaterial;
        [SerializeField] private Material doorIronMaterial;

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

        // The ores' overlays (BlockAtlas.CreateOverlayTexture), the global texture _OreOverlay of the blocks' shader.
        private Texture2D oreOverlays;

        // The smoothed ground (SmoothTerrain): its material (the blocks' shader with _ECO_SMOOTH, with the
        // atlas), the tables it reads (BlockAtlas.SetSmoothTables), whether the meshes now show it, and the
        // chunks' materials with and without it.
        private Material terrainInstance;
        private Texture2D smoothMaterialTable;
        private Texture2D smoothVariantTable;
        private bool smoothShown;

        // Not kept by the Editor's reloads (it would give them back empty, not null).
        [NonSerialized] private Material[] blockMaterials;
        [NonSerialized] private Material[] smoothMaterials;
        private SkyLight skyLight;
        private BlockLight blockLight;
        private TorchViews torchViews;
        private DoorViews doorViews;
        private string savePath;
        private bool rendering;

        public World World { get; private set; }

        // Whether natural ground is drawn smoothed now (SmoothTerrain): as TerrainSettings asks, if the
        // terrain material is set.
        public bool SmoothTerrainShown => smoothShown;

        private bool WantsSmoothTerrain => TerrainSettings.Smooth && terrainInstance != null;

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
            oreOverlays = BlockAtlas.CreateOverlayTexture();
            Shader.SetGlobalTexture("_OreOverlay", oreOverlays);
            Shader.SetGlobalFloat("_DarkestSkyLight", darkestSkyLight);
            BlockAtlas.SetSmoothTables(out smoothMaterialTable, out smoothVariantTable);
            if (terrainMaterial != null)
            {
                terrainInstance = new Material(terrainMaterial) { mainTexture = atlas };
                terrainInstance.EnableKeyword("_ECO_SMOOTH");
            }

            TerrainSettings.Changed += OnTerrainSettingsChanged;
            if (torchWoodMaterial != null && torchHeadMaterial != null && torchFlameMaterial != null)
            {
                torchViews = new TorchViews(torchWoodMaterial, torchHeadMaterial, torchFlameMaterial);
            }

            if (doorWoodMaterial != null && doorIronMaterial != null) doorViews = new DoorViews(doorWoodMaterial, doorIronMaterial);
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
            // The cell above too: one holding a shaped block that stops light (a floor stood on) has none.
            float level = Mathf.Max(skyLight.Get(x, y, z), skyLight.Get(x, y + 1, z)) / (float)SkyLight.Max;
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
            if (World != null && doorViews != null && Camera.main != null) doorViews.Render(World, Camera.main.transform.position);
            if (dirtyChunks.Count == 0) return;

            if (dirtyChunks.Count >= ParallelRemeshCount)
            {
                remeshing.Clear();
                remeshing.AddRange(dirtyChunks);
                ChunkMeshing.BuildAll(World, skyLight, remeshing, ShowMesh, blockLight, smoothShown);
                remeshing.Clear();
            }
            else
            {
                builder ??= new ChunkMeshBuilder();
                foreach (Chunk chunk in dirtyChunks)
                {
                    builder.Build(World, skyLight, chunk, blockLight, smoothShown);
                    ShowMesh(chunk, builder);
                }
            }

            dirtyChunks.Clear();
        }

        private void OnDestroy()
        {
            TerrainSettings.Changed -= OnTerrainSettingsChanged;
            Unload();
            Destroy(materialInstance);
            Destroy(terrainInstance);
            Destroy(atlas);
            Destroy(oreOverlays);
            Destroy(smoothMaterialTable);
            Destroy(smoothVariantTable);
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
            doorViews?.RefreshChunk(chunk);

            // The faces of neighbouring chunks turned toward this one change too; on smoothed ground,
            // those of the chunks touching it by an edge or a corner as well (they share its corners' vertices).
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sides = (dx != 0 ? 1 : 0) + (dy != 0 ? 1 : 0) + (dz != 0 ? 1 : 0);
                        if (sides == 0 || (sides > 1 && !smoothShown)) continue;

                        Chunk neighbour = World.GetChunk(new ChunkCoord(chunk.Coord.X + dx, chunk.Coord.Y + dy, chunk.Coord.Z + dz));
                        if (neighbour != null) dirtyChunks.Add(neighbour);
                    }
                }
            }
        }

        public void Unload()
        {
            if (World == null) return;

            World.BlockChanged -= OnBlockChanged;
            World.Pins.Changed -= OnPinChanged;
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
            doorViews?.Clear();
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
            doorViews?.Reset(World);
            double lightSeconds = timer.Elapsed.TotalSeconds;

            World.BlockChanged += OnBlockChanged;
            World.Pins.Changed += OnPinChanged;
            TerrainSettings.Apply();
            smoothShown = WantsSmoothTerrain;
            ChunkMeshing.BuildAll(World, skyLight, World.Chunks, ShowMesh, blockLight, smoothShown);

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
                int submeshes = filter.sharedMesh != null ? filter.sharedMesh.subMeshCount : 0;
                Destroy(filter.sharedMesh);
                filter.sharedMesh = mesh;
                if (submeshes != mesh.subMeshCount) filter.GetComponent<MeshRenderer>().sharedMaterials = MaterialsFor(mesh);
                return;
            }

            var chunkObject = new GameObject(mesh.name);
            chunkObject.transform.SetParent(transform, false);
            chunkObject.transform.localPosition = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * Chunk.Size;
            filter = chunkObject.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            chunkObject.AddComponent<MeshRenderer>().sharedMaterials = MaterialsFor(mesh);
            chunkMeshes.Add(chunk, filter);
        }

        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            MarkDirty(pos);
            skyLight.Update(pos, dirtyChunks);
            blockLight.Update(pos, dirtyChunks);
            doorViews?.Changed(pos, current);

            // A block on a chunk edge also changes the visible faces of the neighbouring chunk; on
            // smoothed ground, it moves the vertices of its eight corners, which the faces of every
            // block around it share, diagonals included, and a player's block placed or taken also
            // pins or frees the ground under it (SmoothTerrain.IsPinned), so one more layer below.
            // A fence or a low wall also pins or frees the ground beside it (one more column each way).
            if (smoothShown)
            {
                int reach = Shapes.JoinsGround(previous) || Shapes.JoinsGround(current) ? 2 : 1;
                for (int dz = -reach; dz <= reach; dz++)
                {
                    for (int dy = -2; dy <= 1; dy++)
                    {
                        for (int dx = -reach; dx <= reach; dx++)
                        {
                            MarkDirty(new BlockPos(pos.X + dx, pos.Y + dy, pos.Z + dz));
                        }
                    }
                }

                return;
            }

            foreach (BlockPos offset in NeighbourOffsets)
            {
                MarkDirty(pos + offset);
            }
        }

        // Storage was placed on this ground cell or taken from it (World.Pins): it is kept in blocks or
        // smoothed again, and so move the vertices of its corners, shared by every block around it.
        private void OnPinChanged(BlockPos cell)
        {
            if (!smoothShown) return;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        MarkDirty(new BlockPos(cell.X + dx, cell.Y + dy, cell.Z + dz));
                    }
                }
            }
        }

        // The ground is drawn smoothed or in blocks from now on: every chunk is meshed again.
        private void OnTerrainSettingsChanged()
        {
            if (!rendering || World == null) return;

            bool wanted = WantsSmoothTerrain;
            if (wanted == smoothShown && (!wanted || SmoothTerrain.Strength == TerrainSettings.Smoothing)) return;

            TerrainSettings.Apply();
            smoothShown = wanted;
            dirtyChunks.Clear();
            ChunkMeshing.BuildAll(World, skyLight, World.Chunks, ShowMesh, blockLight, smoothShown);
        }

        // The materials of a chunk's object for its mesh's submeshes: the blocks and the water, then the
        // smoothed ground if it has one (a renderer with more materials than submeshes would draw the
        // last one again with each extra material).
        private Material[] MaterialsFor(Mesh mesh)
        {
            if (mesh.subMeshCount > 2) return smoothMaterials ??= new[] { materialInstance, waterMaterial, terrainInstance };
            return blockMaterials ??= new[] { materialInstance, waterMaterial };
        }

        private void MarkDirty(BlockPos pos)
        {
            Chunk chunk = World.GetChunk(ChunkCoord.FromBlock(pos));
            if (chunk != null) dirtyChunks.Add(chunk);
        }
    }
}
