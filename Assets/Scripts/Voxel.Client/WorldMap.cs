using System.Collections.Generic;
using System.Diagnostics;
using Eco.Sim;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The picture of the whole world seen from above, one pixel per block column, shared by the
    // minimap and the full-screen map: the top block of each column, lit from the north-west and
    // lighter the higher it is, with the plants' crowns over it.
    //
    // The terrain is read from this machine's world in tiles of 16 × 16 columns, nearest to the
    // player first, within a time budget per frame; a block change reads its column again. A tile
    // is redrawn into the texture only when something in it changed (a block, a plant, a
    // neighbour it is shaded from), and the texture is uploaded at most ten times a second. So the
    // picture costs nothing while nothing changes, and the maps only move and scale it.
    public class WorldMap : MonoBehaviour
    {
        private const int TileBits = 4;
        private const int TileSize = 1 << TileBits;
        private const double ReadBudgetMilliseconds = 1.5;
        private const double DrawBudgetMilliseconds = 1.5;
        private const float UploadInterval = 0.1f;

        // What has not been read yet, and the ground of the maps beyond the world's edge.
        public static readonly Color32 Unknown = new Color32(43, 49, 56, 255);

        private static readonly int BaseColour = Shader.PropertyToID("_BaseColor");

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;

        private readonly Dictionary<long, Tile> tiles = new Dictionary<long, Tile>();
        private readonly List<long> dirtyTiles = new List<long>();
        private readonly Stopwatch timer = new Stopwatch();

        private World world;
        private Texture2D texture;
        private int unreadTiles;
        private bool changed;
        private float nextUpload;

        // By species (and for plants by model): the colour seen from above and, for plants, the
        // radius of the crown at adult size.
        private Color[][] plantColours;
        private float[][] plantRadii;
        private int plantReach;
        private Color[] animalColours;

        // The picture of the current session's world, or null outside a session. Pixel (x, z) is column (x, z).
        public Texture2D Texture => texture;

        public World World => world;

        internal Color AnimalColour(int speciesIndex)
        {
            return speciesIndex >= 0 && speciesIndex < animalColours.Length ? animalColours[speciesIndex] : Color.magenta;
        }

        // Plants are announced from the start of a session, possibly before other objects' Start.
        private void Awake()
        {
            ReadSpeciesLooks();
            ecosystemSync.PlantShown += OnPlantShown;
            ecosystemSync.PlantHidden += OnPlantHidden;
            ecosystemSync.PlantsCleared += OnPlantsCleared;

            // A dedicated server has no one to show a map to.
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            if (ecosystemSync != null)
            {
                ecosystemSync.PlantShown -= OnPlantShown;
                ecosystemSync.PlantHidden -= OnPlantHidden;
                ecosystemSync.PlantsCleared -= OnPlantsCleared;
            }

            Show(null);
        }

        private void Update()
        {
            World current = worldSync.World;
            if (current != world) Show(current);
            if (world == null) return;

            if (unreadTiles > 0) ReadTiles();
            if (dirtyTiles.Count > 0) DrawTiles();

            if (changed && Time.unscaledTime >= nextUpload)
            {
                texture.Apply(false);
                changed = false;
                nextUpload = Time.unscaledTime + UploadInterval;
            }
        }

        // Follows the world of the current session: the terrain of another world is dropped.
        // Plants are kept: they follow their own events.
        private void Show(World shown)
        {
            if (world != null) world.BlockChanged -= OnBlockChanged;
            world = shown;

            foreach (Tile tile in tiles.Values)
            {
                tile.Heights = null;
                tile.Tops = null;
                tile.Depths = null;
                tile.Dirty = false;
            }

            dirtyTiles.Clear();
            changed = false;
            if (texture != null) Destroy(texture);
            texture = null;
            if (world == null) return;

            world.BlockChanged += OnBlockChanged;
            unreadTiles = (world.SizeX >> TileBits) * (world.SizeZ >> TileBits);
            texture = new Texture2D(world.SizeX, world.SizeZ, TextureFormat.RGBA32, false)
            {
                name = "WorldMap",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            NativeArray<Color32> pixels = texture.GetPixelData<Color32>(0);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Unknown;
            }

            texture.Apply(false);
        }

        private void OnBlockChanged(BlockPos pos, ushort previous, ushort block)
        {
            if (!tiles.TryGetValue(KeyOf(pos.X >> TileBits, pos.Z >> TileBits), out Tile tile) || tile.Heights == null) return;

            ReadColumn(tile, pos.X, pos.Z);

            // The column south-east of this one is shaded from it.
            MarkDirty(pos.X, pos.Z - 1, pos.X + 1, pos.Z);
        }

        private void OnPlantShown(int plantId, PlantView plant)
        {
            TileAt(plant.X >> TileBits, plant.Z >> TileBits).Plants[plantId] = plant;
            MarkDirty(plant.X - plantReach, plant.Z - plantReach, plant.X + plantReach, plant.Z + plantReach);
        }

        private void OnPlantHidden(int plantId, PlantView plant)
        {
            if (!tiles.TryGetValue(KeyOf(plant.X >> TileBits, plant.Z >> TileBits), out Tile tile)) return;

            tile.Plants.Remove(plantId);
            MarkDirty(plant.X - plantReach, plant.Z - plantReach, plant.X + plantReach, plant.Z + plantReach);
        }

        private void OnPlantsCleared()
        {
            foreach (KeyValuePair<long, Tile> entry in tiles)
            {
                entry.Value.Plants.Clear();
                if (entry.Value.Heights != null && !entry.Value.Dirty)
                {
                    entry.Value.Dirty = true;
                    dirtyTiles.Add(entry.Key);
                }
            }
        }

        // Asks for the tiles covering these columns to be drawn again, if they have been read.
        private void MarkDirty(int minX, int minZ, int maxX, int maxZ)
        {
            if (world == null) return;

            for (int tileZ = Mathf.Max(0, minZ) >> TileBits; tileZ <= Mathf.Min(world.SizeZ - 1, maxZ) >> TileBits; tileZ++)
            {
                for (int tileX = Mathf.Max(0, minX) >> TileBits; tileX <= Mathf.Min(world.SizeX - 1, maxX) >> TileBits; tileX++)
                {
                    long key = KeyOf(tileX, tileZ);
                    if (!tiles.TryGetValue(key, out Tile tile) || tile.Heights == null || tile.Dirty) continue;

                    tile.Dirty = true;
                    dirtyTiles.Add(key);
                }
            }
        }

        // Reads the tiles not read yet, nearest to the player first, ring after ring.
        private void ReadTiles()
        {
            int tilesX = world.SizeX >> TileBits;
            int tilesZ = world.SizeZ >> TileBits;
            Vector3 focus = FocusPoint();
            int centreX = Mathf.Clamp(Mathf.FloorToInt(focus.x) >> TileBits, 0, tilesX - 1);
            int centreZ = Mathf.Clamp(Mathf.FloorToInt(focus.z) >> TileBits, 0, tilesZ - 1);
            int rings = Mathf.Max(Mathf.Max(centreX, tilesX - 1 - centreX), Mathf.Max(centreZ, tilesZ - 1 - centreZ));

            timer.Restart();
            for (int ring = 0; ring <= rings; ring++)
            {
                int side = 2 * ring;
                int count = ring == 0 ? 1 : 4 * side;
                for (int i = 0; i < count; i++)
                {
                    RingTile(centreX, centreZ, ring, i, out int tileX, out int tileZ);
                    if (tileX < 0 || tileZ < 0 || tileX >= tilesX || tileZ >= tilesZ) continue;

                    Tile tile = TileAt(tileX, tileZ);
                    if (tile.Heights != null) continue;

                    ReadTile(tile, tileX, tileZ);
                    unreadTiles--;
                    MarkDirty(tileX * TileSize, tileZ * TileSize - 1, tileX * TileSize + TileSize, tileZ * TileSize + TileSize - 1);
                    if (unreadTiles == 0 || timer.Elapsed.TotalMilliseconds >= ReadBudgetMilliseconds) return;
                }
            }
        }

        // The index-th tile on the square ring at this distance around the centre, going round its four sides.
        private static void RingTile(int centreX, int centreZ, int ring, int index, out int tileX, out int tileZ)
        {
            if (ring == 0)
            {
                tileX = centreX;
                tileZ = centreZ;
                return;
            }

            int side = 2 * ring;
            int along = index % side;
            switch (index / side)
            {
                case 0:
                    tileX = centreX - ring + along;
                    tileZ = centreZ - ring;
                    break;
                case 1:
                    tileX = centreX + ring;
                    tileZ = centreZ - ring + along;
                    break;
                case 2:
                    tileX = centreX + ring - along;
                    tileZ = centreZ + ring;
                    break;
                default:
                    tileX = centreX - ring;
                    tileZ = centreZ + ring - along;
                    break;
            }
        }

        // Where the player stands, or the middle of the world before they are there.
        private Vector3 FocusPoint()
        {
            NetworkObject player = networkManager.IsListening ? networkManager.SpawnManager?.GetLocalPlayerObject() : null;
            return player != null ? player.transform.position : new Vector3(world.SizeX * 0.5f, 0f, world.SizeZ * 0.5f);
        }

        private void ReadTile(Tile tile, int tileX, int tileZ)
        {
            tile.Heights = new short[TileSize * TileSize];
            tile.Tops = new ushort[TileSize * TileSize];
            tile.Depths = new byte[TileSize * TileSize];
            for (int z = 0; z < TileSize; z++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    ReadColumn(tile, tileX * TileSize + x, tileZ * TileSize + z);
                }
            }
        }

        // The top of the column as seen from above, water included, and how deep the water is there.
        private void ReadColumn(Tile tile, int x, int z)
        {
            int column = ColumnOf(x, z);
            int height = world.GetTopHeight(x, z);
            ushort top = height > 0 ? Blocks.TypeOf(world.GetBlock(x, height - 1, z)) : Blocks.Air;
            tile.Heights[column] = (short)height;
            tile.Tops[column] = top;
            tile.Depths[column] = top == Blocks.Water ? (byte)Mathf.Min(255, height - world.GetSurfaceHeight(x, z)) : (byte)0;
        }

        private void DrawTiles()
        {
            NativeArray<Color32> pixels = texture.GetPixelData<Color32>(0);
            timer.Restart();
            int drawn = 0;
            while (drawn < dirtyTiles.Count && timer.Elapsed.TotalMilliseconds < DrawBudgetMilliseconds)
            {
                long key = dirtyTiles[drawn++];
                if (!tiles.TryGetValue(key, out Tile tile) || tile.Heights == null) continue;

                tile.Dirty = false;
                DrawTile(tile, (int)(key >> 32), (int)(uint)key, pixels);
            }

            dirtyTiles.RemoveRange(0, drawn);
            if (drawn > 0) changed = true;
        }

        private void DrawTile(Tile tile, int tileX, int tileZ, NativeArray<Color32> pixels)
        {
            int minX = tileX * TileSize;
            int minZ = tileZ * TileSize;
            float heightScale = 1f / world.SizeY;
            Geography geography = world.Geography;

            for (int z = minZ; z < minZ + TileSize; z++)
            {
                for (int x = minX; x < minX + TileSize; x++)
                {
                    int column = ColumnOf(x, z);
                    int height = tile.Heights[column];
                    Color32 colour = Unknown;
                    if (height > 0)
                    {
                        // A column higher than its north-west neighbour faces the light.
                        int northWest = HeightAt(tile, tileX, tileZ, x - 1, z + 1);
                        int slope = northWest > 0 ? Mathf.Clamp(height - northWest, -3, 3) : 0;
                        float light = 0.72f + 0.45f * height * heightScale + 0.07f * slope;
                        // Grass has the colour of its climate, as in the world; water is flat and darker when deep.
                        ushort top = tile.Tops[column];
                        Color ground;
                        if (top == Blocks.Water)
                        {
                            ground = BlockAtlas.WaterColour(tile.Depths[column]);
                            light = 1f;
                        }
                        else
                        {
                            ground = top == Blocks.Grass
                                ? ClimateColours.Grass(geography.TemperatureAt(x, z), geography.HumidityAt(x, z))
                                : BlockAtlas.MapColour(top);
                        }

                        Color shaded = ground * light;
                        shaded.a = 1f;
                        colour = shaded;
                    }

                    pixels[z * world.SizeX + x] = colour;
                }
            }

            // Crowns reach over the tile's edges: the plants of the neighbouring tiles are drawn too, cut to this one.
            int reachTiles = (plantReach + TileSize - 1) >> TileBits;
            for (int otherZ = tileZ - reachTiles; otherZ <= tileZ + reachTiles; otherZ++)
            {
                for (int otherX = tileX - reachTiles; otherX <= tileX + reachTiles; otherX++)
                {
                    if (!tiles.TryGetValue(KeyOf(otherX, otherZ), out Tile other)) continue;

                    foreach (PlantView plant in other.Plants.Values)
                    {
                        DrawPlant(plant, minX, minZ, pixels);
                    }
                }
            }
        }

        private int HeightAt(Tile tile, int tileX, int tileZ, int x, int z)
        {
            if (!world.InBounds(x, 0, z)) return -1;
            if (x >> TileBits != tileX || z >> TileBits != tileZ)
            {
                if (!tiles.TryGetValue(KeyOf(x >> TileBits, z >> TileBits), out tile)) return -1;
            }

            return tile.Heights != null ? tile.Heights[ColumnOf(x, z)] : -1;
        }

        // A round crown over the plant's column, as wide as the plant looks from above at its stage,
        // lit from the north-west like the terrain. Only the part inside the tile starting at
        // (minX, minZ) is drawn.
        private void DrawPlant(PlantView plant, int minX, int minZ, NativeArray<Color32> pixels)
        {
            if (plant.SpeciesIndex >= plantColours.Length || plantColours[plant.SpeciesIndex].Length == 0) return;

            // The model the plant shows, as the plant renderer picks it.
            int model = ecosystemSync.PlantAssets[plant.SpeciesIndex].ModelAt(plant.X, plant.Z);
            float radius = Mathf.Max(0.5f, plantRadii[plant.SpeciesIndex][model] * PlantShape.Scale(plant.Stage));
            int reach = Mathf.FloorToInt(radius);
            float limit = radius * radius + 0.5f;
            Color colour = plantColours[plant.SpeciesIndex][model];

            for (int dz = -reach; dz <= reach; dz++)
            {
                int z = plant.Z + dz;
                if (z < minZ || z >= minZ + TileSize) continue;

                for (int dx = -reach; dx <= reach; dx++)
                {
                    int x = plant.X + dx;
                    if (x < minX || x >= minX + TileSize || dx * dx + dz * dz > limit) continue;

                    Color shaded = colour * (1f + 0.2f * (dz - dx) / (reach + 1f));
                    shaded.a = 1f;
                    pixels[z * world.SizeX + x] = shaded;
                }
            }
        }

        // What each species looks like from above: the colour of its widest part and, for a plant,
        // how far its parts reach from the centre of its column; for a plant, once per model.
        private void ReadSpeciesLooks()
        {
            IReadOnlyList<SpeciesAsset> plantAssets = ecosystemSync.PlantAssets;
            plantColours = new Color[plantAssets.Count][];
            plantRadii = new float[plantAssets.Count][];
            for (int s = 0; s < plantAssets.Count; s++)
            {
                int models = plantAssets[s].ModelCount;
                plantColours[s] = new Color[models];
                plantRadii[s] = new float[models];
                for (int m = 0; m < models; m++)
                {
                    IReadOnlyList<SpeciesAsset.Part> parts = plantAssets[s].PartsOf(m);
                    plantColours[s][m] = ColourOf(parts) * 0.9f;
                    plantRadii[s][m] = ReachOf(parts);
                    plantReach = Mathf.Max(plantReach, Mathf.CeilToInt(plantRadii[s][m]));
                }
            }

            IReadOnlyList<AnimalSpeciesAsset> animalAssets = ecosystemSync.AnimalAssets;
            animalColours = new Color[animalAssets.Count];
            for (int s = 0; s < animalAssets.Count; s++)
            {
                animalColours[s] = ColourOf(animalAssets[s].Parts);
            }
        }

        // How far the parts reach, horizontally, from the centre of the plant's column.
        private static float ReachOf(IReadOnlyList<SpeciesAsset.Part> parts)
        {
            float radius = 0f;
            foreach (SpeciesAsset.Part part in parts)
            {
                if (part.mesh == null) continue;

                Bounds bounds = part.mesh.bounds;
                float reachX = Mathf.Max(Mathf.Abs(bounds.min.x), Mathf.Abs(bounds.max.x)) * part.scale.x;
                float reachZ = Mathf.Max(Mathf.Abs(bounds.min.z), Mathf.Abs(bounds.max.z)) * part.scale.z;
                radius = Mathf.Max(radius, Mathf.Max(Mathf.Abs(part.offset.x), Mathf.Abs(part.offset.z)) + Mathf.Max(reachX, reachZ));
            }

            return radius;
        }

        // The colour of the part that shows most from above.
        private static Color ColourOf(IReadOnlyList<SpeciesAsset.Part> parts)
        {
            Material widest = null;
            float widestArea = -1f;
            foreach (SpeciesAsset.Part part in parts)
            {
                if (part.mesh == null || part.material == null) continue;

                float area = TopArea(part.mesh) * part.scale.x * part.scale.z;
                if (area <= widestArea) continue;

                widest = part.material;
                widestArea = area;
            }

            if (widest == null) return Color.magenta;
            Color colour = widest.HasProperty(BaseColour) ? widest.GetColor(BaseColour) : widest.color;
            colour.a = 1f;
            return colour;
        }

        // The area of the mesh's faces turned upwards, as seen from above. Scattered small pieces
        // (berries) count for what they cover, not for the box around them. A mesh whose data cannot
        // be read counts as its bounds.
        private static float TopArea(Mesh mesh)
        {
            if (!mesh.isReadable) return mesh.bounds.size.x * mesh.bounds.size.z;

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            float area = 0f;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector3 a = vertices[triangles[i]];
                Vector3 cross = Vector3.Cross(vertices[triangles[i + 1]] - a, vertices[triangles[i + 2]] - a);
                if (cross.y > 0f) area += cross.y * 0.5f;
            }

            return area;
        }

        private Tile TileAt(int tileX, int tileZ)
        {
            long key = KeyOf(tileX, tileZ);
            if (!tiles.TryGetValue(key, out Tile tile))
            {
                tile = new Tile();
                tiles.Add(key, tile);
            }

            return tile;
        }

        private static int ColumnOf(int x, int z) => (x & (TileSize - 1)) + TileSize * (z & (TileSize - 1));

        private static long KeyOf(int tileX, int tileZ) => ((long)tileX << 32) | (uint)tileZ;

        // 16 × 16 columns: their surface (null until read from the world), the plants standing
        // there, and whether the tile waits to be drawn again.
        private sealed class Tile
        {
            public readonly Dictionary<int, PlantView> Plants = new Dictionary<int, PlantView>();
            public short[] Heights;
            public ushort[] Tops;
            public byte[] Depths;
            public bool Dirty;
        }
    }
}
