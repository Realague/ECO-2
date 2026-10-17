using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Placeholder atlas generated at startup: one tile per block appearance, drawn by a few simple
    // rules, in a single row of 64 × 64-pixel tiles, with mipmaps (CreateTexture). Most block types
    // have one tile for all their faces (TypeTiles); grass has a top, sides and the dirt below. Pixel
    // noise gives soils their grain; rocks are drawn like pixel art (BlockAtlas.Rocks).
    internal static partial class BlockAtlas
    {
        private const int TileSize = 64;

        // The smallest tiles the mipmaps go down to.
        private const int MipTileSize = 4;

        private const int GraniteTile = 0;
        private const int DirtTile = 1;
        private const int GrassTopTile = 2;
        private const int GrassSideTile = 3;
        private const int IceTile = 4;
        private const int BasaltTile = 5;
        private const int LimestoneTile = 6;
        private const int MarbleTile = 7;
        private const int SandstoneTile = 8;
        private const int SlateTile = 9;
        private const int SandTile = 10;
        private const int SnowTile = 11;
        private const int GravelTile = 12;
        private const int ClayTile = 13;
        private const int CoalTile = 14;
        private const int IronTile = 15;
        private const int CopperTile = 16;
        private const int TinTile = 17;
        private const int GoldTile = 18;
        private const int SaltTile = 19;
        private const int FlintTile = 20;
        private const int BedrockTile = 21;

        public const int TileCount = 22;

        // The tile of each block type for all its faces; grass is handled apart.
        private static readonly int[] TypeTiles = BuildTypeTiles();

        // Depth of the green band at the top of a grass block's sides, in pixels, give or take its ragged edge.
        private const int GrassSideBand = 16;

        private static readonly Color Granite = new Color(0.60f, 0.55f, 0.53f);
        private static readonly Color Dirt = new Color(0.47f, 0.33f, 0.20f);
        private static readonly Color Grass = new Color(0.33f, 0.60f, 0.25f);

        // Tinted pixels are drawn light grey: the climate's colour, multiplied in by the shader, makes their colour.
        private static readonly Color TintBase = new Color(0.97f, 0.97f, 0.97f);
        private static readonly Color Ice = new Color(0.70f, 0.85f, 0.95f);
        private static readonly Color IceCrack = new Color(0.86f, 0.94f, 1.00f);
        private static readonly Color Basalt = new Color(0.25f, 0.25f, 0.27f);
        private static readonly Color Limestone = new Color(0.82f, 0.78f, 0.66f);
        private static readonly Color Marble = new Color(0.93f, 0.93f, 0.91f);
        private static readonly Color Sandstone = new Color(0.86f, 0.63f, 0.40f);
        private static readonly Color Slate = new Color(0.37f, 0.41f, 0.47f);
        private static readonly Color Sand = new Color(0.88f, 0.80f, 0.56f);
        private static readonly Color Snow = new Color(0.95f, 0.97f, 1.00f);
        private static readonly Color Gravel = new Color(0.53f, 0.51f, 0.49f);
        private static readonly Color Clay = new Color(0.62f, 0.59f, 0.57f);
        private static readonly Color Bedrock = new Color(0.17f, 0.17f, 0.19f);

        // The colour of water on the maps, shallow and deep.
        private static readonly Color ShallowWater = new Color(0.30f, 0.55f, 0.75f);
        private static readonly Color DeepWater = new Color(0.10f, 0.25f, 0.50f);

        public static int TileFor(ushort block, int face)
        {
            ushort type = Blocks.TypeOf(block);
            if (type == Blocks.Grass)
            {
                if (face == ChunkMesher.TopFace) return GrassTopTile;
                return face == ChunkMesher.BottomFace ? DirtTile : GrassSideTile;
            }

            return type < TypeTiles.Length ? TypeTiles[type] : GraniteTile;
        }

        // Colour of water seen from above at this depth, in blocks, for the maps.
        public static Color WaterColour(int depth)
        {
            return Color.Lerp(ShallowWater, DeepWater, Mathf.Clamp01((depth - 1) / 8f));
        }

        // Colour of a block seen from far above, for the maps. Grass varies with the climate: the maps
        // use ClimateColours.Grass for it, this is the colour of a mild climate.
        public static Color MapColour(ushort block)
        {
            switch (Blocks.TypeOf(block))
            {
                case Blocks.Dirt: return Dirt;
                case Blocks.Grass: return Grass;
                case Blocks.Ice: return Ice;
                case Blocks.Water: return ShallowWater;
                case Blocks.Basalt: return Basalt;
                case Blocks.Limestone: return Limestone;
                case Blocks.Marble: return Marble;
                case Blocks.Sandstone: return Sandstone;
                case Blocks.Slate: return Slate;
                case Blocks.Sand: return Sand;
                case Blocks.Snow: return Snow;
                case Blocks.Gravel: return Gravel;
                case Blocks.Clay: return Clay;
                case Blocks.Bedrock: return Bedrock;
                default: return Granite;
            }
        }


        // The atlas with its mipmaps, each level made tile by tile (a pixel averages the 2 × 2 under it
        // within its own tile), down to tiles of MipTileSize: blocks stay sharp up close (point
        // filtering), do not shimmer far away, and no colour ever bleeds from one tile into the next.
        public static Texture2D CreateTexture()
        {
            int mipCount = 1;
            for (int size = TileSize; size > MipTileSize; size /= 2) mipCount++;

            var texture = new Texture2D(TileCount * TileSize, TileSize, TextureFormat.RGBA32, mipCount, false)
            {
                name = "BlockAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            int width = TileCount * TileSize;
            var level = new Color[width * TileSize];
            for (int tile = 0; tile < TileCount; tile++)
            {
                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        Color color = IsPainted(tile) ? PaintedColour(tile, x, y) : BaseColor(tile, x, y) * (0.9f + 0.1f * Noise(tile, x, y));

                        // Alpha is not transparency but the tint mask read by the blocks' shader:
                        // below 1, the pixel takes the colour of the climate where the block stands.
                        color.a = IsTinted(tile, x, y) ? 0f : 1f;
                        level[y * width + tile * TileSize + x] = color;
                    }
                }
            }

            texture.SetPixels(level, 0);
            int tileSize = TileSize;
            for (int mip = 1; mip < mipCount; mip++)
            {
                level = HalveTiles(level, tileSize);
                tileSize /= 2;
                texture.SetPixels(level, mip);
            }

            texture.Apply(false, true);
            return texture;
        }

        // The next mipmap level of a row of tiles of this size: each pixel the mean of the four under it.
        private static Color[] HalveTiles(Color[] pixels, int tileSize)
        {
            int width = TileCount * tileSize;
            int half = tileSize / 2;
            int halfWidth = TileCount * half;
            var result = new Color[halfWidth * half];
            for (int y = 0; y < half; y++)
            {
                for (int x = 0; x < halfWidth; x++)
                {
                    int sourceX = x * 2;
                    int sourceY = y * 2;
                    result[y * halfWidth + x] = (pixels[sourceY * width + sourceX] + pixels[sourceY * width + sourceX + 1]
                        + pixels[(sourceY + 1) * width + sourceX] + pixels[(sourceY + 1) * width + sourceX + 1]) * 0.25f;
                }
            }

            return result;
        }

        private static int[] BuildTypeTiles()
        {
            var tiles = new int[Blocks.LastType + 1];
            tiles[Blocks.Granite] = GraniteTile;
            tiles[Blocks.Dirt] = DirtTile;
            tiles[Blocks.Ice] = IceTile;
            tiles[Blocks.Basalt] = BasaltTile;
            tiles[Blocks.Limestone] = LimestoneTile;
            tiles[Blocks.Marble] = MarbleTile;
            tiles[Blocks.Sandstone] = SandstoneTile;
            tiles[Blocks.Slate] = SlateTile;
            tiles[Blocks.Sand] = SandTile;
            tiles[Blocks.Snow] = SnowTile;
            tiles[Blocks.Gravel] = GravelTile;
            tiles[Blocks.Clay] = ClayTile;
            tiles[Blocks.CoalOre] = CoalTile;
            tiles[Blocks.IronOre] = IronTile;
            tiles[Blocks.CopperOre] = CopperTile;
            tiles[Blocks.TinOre] = TinTile;
            tiles[Blocks.GoldOre] = GoldTile;
            tiles[Blocks.RockSalt] = SaltTile;
            tiles[Blocks.Flint] = FlintTile;
            tiles[Blocks.Bedrock] = BedrockTile;
            return tiles;
        }


        // Soils and ice; rocks and snow are in BlockAtlas.Rocks. Repeating patterns have periods that
        // divide the tile, so they run on unbroken from one block to the next. Sizes are in pixels of a 64-pixel tile.
        private static Color BaseColor(int tile, int x, int y)
        {
            float n = Noise(tile + 100, x, y);
            float crumb = Noise(tile + 300, x / 2, y / 2);
            float pebble = Noise(tile + 800, x / 4, y / 4);
            switch (tile)
            {
                case DirtTile:
                    return DirtAt(crumb, pebble);
                case GrassTopTile:
                    // Blades: short streaks of darker and lighter pixels in the climate's colour.
                    float blade = Noise(tile + 400, x, y / 3);
                    return blade < 0.15f ? TintBase * 0.84f : blade > 0.88f ? TintBase * 1.03f : n < 0.1f ? TintBase * 0.92f : TintBase;
                case GrassSideTile:
                    return IsTinted(tile, x, y) ? TintBase : DirtAt(crumb, pebble);
                case IceTile:
                    // Cracks, bubbles, and a faint frost of lighter pixels.
                    if (IsCrack(x, y)) return IceCrack;
                    return n > 0.93f ? Color.Lerp(Ice, IceCrack, 0.6f) : SmoothNoise(tile + 400, x, y, 16) > 0.7f ? Color.Lerp(Ice, IceCrack, 0.25f) : Ice;
                case SandTile:
                    return n > 0.85f ? Sand * 0.88f : n < 0.06f ? Sand * 1.08f : crumb > 0.95f ? Sand * 0.8f : Sand;
                case ClayTile:
                    // Smooth, with faint horizontal streaks.
                    return Noise(tile + 400, x / 16, y / 2) > 0.8f ? Clay * 0.94f : Clay;
                default:
                    return PaintedColour(tile, x, y);
            }
        }

        // Dirt with darker crumbs and a few light pebbles.
        private static Color DirtAt(float crumb, float pebble)
        {
            if (pebble > 0.93f) return Dirt * 1.2f;
            return crumb < 0.15f ? Dirt * 0.78f : crumb > 0.9f ? Dirt * 1.1f : Dirt;
        }

        // Grass: the top, and the band at the top of the sides, its lower edge ragged: each run of
        // three columns hangs down zero to five pixels further.
        private static bool IsTinted(int tile, int x, int y)
        {
            if (tile == GrassTopTile) return true;
            if (tile != GrassSideTile) return false;

            int fringe = (int)(Noise(tile + 500, x / 3, 0) * 6f);
            return y >= TileSize - GrassSideBand - fringe;
        }

        // Noise drawn every `cell` pixels and blended in between, wrapping around the tile so that
        // neighbouring blocks match.
        private static float SmoothNoise(int tile, int x, int y, int cell)
        {
            int cells = TileSize / cell;
            int x0 = x / cell;
            int y0 = y / cell;
            float tx = (x % cell + 0.5f) / cell;
            float ty = (y % cell + 0.5f) / cell;
            float a = Noise(tile, x0 % cells, y0 % cells);
            float b = Noise(tile, (x0 + 1) % cells, y0 % cells);
            float c = Noise(tile, x0 % cells, (y0 + 1) % cells);
            float d = Noise(tile, (x0 + 1) % cells, (y0 + 1) % cells);
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        // A few light streaks across the ice, one pixel wide.
        private static bool IsCrack(int x, int y)
        {
            return (x + 2 * y) % 44 == 0 || (3 * x - y + 64) % 52 == 0;
        }

        private static float Noise(int tile, int x, int y)
        {
            int hash = (x * 73856093) ^ (y * 19349663) ^ (tile * 83492791);
            hash ^= hash >> 13;
            hash *= 1274126177;
            return ((hash >> 8) & 0xFF) / 255f;
        }
    }
}
