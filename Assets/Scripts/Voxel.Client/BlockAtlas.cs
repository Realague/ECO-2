using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // The blocks' textures: one tile per block appearance, read from the image files in
    // Assets/Resources/Blocks ("<name>.png" for each of TileNames), gathered into a single row of
    // square tiles with mipmaps (CreateTexture). Most block types have one tile for all their faces
    // (TypeTiles); grass has a top, sides and the dirt below. A tile may have a tint mask,
    // "<name>_Tint.png": where it is white, the pixel takes the colour of the climate where the block
    // stands (grass), so it is drawn light grey in the tile. The files can be edited or replaced
    // freely, at any size; Eco > Write missing block textures (BlockTextureBuilder) paints any that are missing.
    public static class BlockAtlas
    {
        // Under a Resources folder, so the files go into builds without being referenced.
        public const string ResourceFolder = "Blocks";
        public const string TintSuffix = "_Tint";

        // The smallest tiles the mipmaps go down to.
        private const int MipTileSize = 4;

        public const int GraniteTile = 0;
        public const int DirtTile = 1;
        public const int GrassTopTile = 2;
        public const int GrassSideTile = 3;
        public const int IceTile = 4;
        public const int BasaltTile = 5;
        public const int LimestoneTile = 6;
        public const int MarbleTile = 7;
        public const int SandstoneTile = 8;
        public const int SlateTile = 9;
        public const int SandTile = 10;
        public const int SnowTile = 11;
        public const int GravelTile = 12;
        public const int ClayTile = 13;
        public const int CoalTile = 14;
        public const int IronTile = 15;
        public const int CopperTile = 16;
        public const int TinTile = 17;
        public const int GoldTile = 18;
        public const int SaltTile = 19;
        public const int FlintTile = 20;
        public const int BedrockTile = 21;
        public const int MudTile = 22;
        public const int TilledSoilTopTile = 23;

        public const int TileCount = 24;

        // The file of each tile, in tile order.
        public static readonly string[] TileNames =
        {
            "Granite", "Dirt", "GrassTop", "GrassSide", "Ice", "Basalt", "Limestone", "Marble", "Sandstone",
            "Slate", "Sand", "Snow", "Gravel", "Clay", "CoalOre", "IronOre", "CopperOre", "TinOre", "GoldOre",
            "RockSalt", "Flint", "Bedrock", "Mud", "TilledSoilTop"
        };

        // The tile of each block type for all its faces; grass is handled apart.
        private static readonly int[] TypeTiles = BuildTypeTiles();

        // On the maps: grass (whose colour is the climate's, ClimateColours.Grass; this is a mild one), water
        // shallow and deep, and a block whose tile is not loaded. Other blocks take their tile's mean colour.
        private static readonly Color Grass = new Color(0.33f, 0.60f, 0.25f);
        private static readonly Color ShallowWater = new Color(0.30f, 0.55f, 0.75f);
        private static readonly Color DeepWater = new Color(0.10f, 0.25f, 0.50f);
        private static readonly Color Unknown = new Color(0.60f, 0.55f, 0.53f);

        // A missing file is drawn as this checkerboard, so it shows.
        private static readonly Color MissingA = new Color(1f, 0f, 1f);
        private static readonly Color MissingB = new Color(0.1f, 0f, 0.1f);

        // The mean colour of each tile's pixels, set by CreateTexture.
        private static Color[] meanColours;

        public static int TileFor(ushort block, int face)
        {
            ushort type = Blocks.TypeOf(block);
            if (type == Blocks.Grass)
            {
                if (face == ChunkMesher.TopFace) return GrassTopTile;
                return face == ChunkMesher.BottomFace ? DirtTile : GrassSideTile;
            }

            // Tilled soil shows its furrows on top, dirt elsewhere.
            if (type == Blocks.TilledSoil) return face == ChunkMesher.TopFace ? TilledSoilTopTile : DirtTile;

            return type < TypeTiles.Length ? TypeTiles[type] : GraniteTile;
        }

        // Colour of water seen from above at this depth, in blocks, for the maps.
        public static Color WaterColour(int depth)
        {
            return Color.Lerp(ShallowWater, DeepWater, Mathf.Clamp01((depth - 1) / 8f));
        }

        // Colour of a block seen from far above, for the maps: the mean of its top's tile. Grass varies
        // with the climate: the maps use ClimateColours.Grass for it.
        public static Color MapColour(ushort block)
        {
            switch (Blocks.TypeOf(block))
            {
                case Blocks.Grass: return Grass;
                case Blocks.Water: return ShallowWater;
                default: return meanColours != null ? meanColours[TileFor(block, ChunkMesher.TopFace)] : Unknown;
            }
        }

        // Colour of the chips a hit on this face of a block sends flying (ChipBursts): the mean of the
        // face's tile; grass is green on top and earth elsewhere (its sides' green band is tinted by the
        // climate, so its tile alone is grey).
        public static Color ChipColour(ushort block, int face)
        {
            if (Blocks.TypeOf(block) == Blocks.Grass)
            {
                if (face == ChunkMesher.TopFace) return Grass;
                return meanColours != null ? meanColours[DirtTile] : Unknown;
            }

            return meanColours != null ? meanColours[TileFor(block, face)] : Unknown;
        }

        // The atlas with its mipmaps, each level made tile by tile (a pixel averages the 2 × 2 under it
        // within its own tile), down to tiles of MipTileSize: blocks stay sharp up close (point
        // filtering), do not shimmer far away, and no colour ever bleeds from one tile into the next.
        // Tiles are as large as the largest file (rounded up to a power of two); smaller files are
        // scaled up pixel by pixel.
        public static Texture2D CreateTexture()
        {
            var colours = new Texture2D[TileCount];
            var masks = new Texture2D[TileCount];
            int tileSize = MipTileSize;
            for (int tile = 0; tile < TileCount; tile++)
            {
                string path = $"{ResourceFolder}/{TileNames[tile]}";
                colours[tile] = Load(path, true);
                masks[tile] = Load(path + TintSuffix, false);
                if (colours[tile] != null) tileSize = Mathf.Max(tileSize, colours[tile].width, colours[tile].height);
            }

            tileSize = Mathf.NextPowerOfTwo(tileSize);
            int mipCount = 1;
            for (int size = tileSize; size > MipTileSize; size /= 2) mipCount++;

            var texture = new Texture2D(TileCount * tileSize, tileSize, TextureFormat.RGBA32, mipCount, false)
            {
                name = "BlockAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            int width = TileCount * tileSize;
            var level = new Color[width * tileSize];
            meanColours = new Color[TileCount];
            for (int tile = 0; tile < TileCount; tile++)
            {
                Color sum = Color.clear;
                for (int y = 0; y < tileSize; y++)
                {
                    for (int x = 0; x < tileSize; x++)
                    {
                        Color colour = colours[tile] != null
                            ? Sample(colours[tile], x, y, tileSize)
                            : ((x * 8 / tileSize + y * 8 / tileSize) % 2 == 0 ? MissingA : MissingB);

                        // Alpha is not transparency but the tint mask read by the blocks' shader:
                        // below 1, the pixel takes the colour of the climate where the block stands.
                        colour.a = masks[tile] != null && Sample(masks[tile], x, y, tileSize).r >= 0.5f ? 0f : 1f;
                        level[y * width + tile * tileSize + x] = colour;
                        sum += colour;
                    }
                }

                Color mean = sum / (tileSize * tileSize);
                mean.a = 1f;
                meanColours[tile] = mean;
            }

            for (int tile = 0; tile < TileCount; tile++)
            {
                if (colours[tile] != null) Resources.UnloadAsset(colours[tile]);
                if (masks[tile] != null) Resources.UnloadAsset(masks[tile]);
            }

            texture.SetPixels(level, 0);
            for (int mip = 1; mip < mipCount; mip++)
            {
                level = HalveTiles(level, tileSize);
                tileSize /= 2;
                texture.SetPixels(level, mip);
            }

            texture.Apply(false, true);
            return texture;
        }

        // A texture from Resources whose pixels can be read; a missing colour file is reported (a missing mask is not).
        private static Texture2D Load(string path, bool required)
        {
            var texture = Resources.Load<Texture2D>(path);
            if (texture == null)
            {
                if (required) Debug.LogError($"Block texture Resources/{path}.png is missing: drawn as a checkerboard.");
                return null;
            }

            if (!texture.isReadable)
            {
                Debug.LogError($"Block texture Resources/{path}.png cannot be read: tick Read/Write in its import settings.");
                Resources.UnloadAsset(texture);
                return null;
            }

            return texture;
        }

        // The file's pixel under this pixel of a tile of the given size (nearest, so pixel art stays sharp).
        private static Color Sample(Texture2D texture, int x, int y, int tileSize)
        {
            return texture.GetPixel(x * texture.width / tileSize, y * texture.height / tileSize);
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
            tiles[Blocks.Mud] = MudTile;
            tiles[Blocks.TilledSoil] = DirtTile;
            return tiles;
        }
    }
}
