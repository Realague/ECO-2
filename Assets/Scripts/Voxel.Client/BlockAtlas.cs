using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Atlas provisoire généré au lancement : une tuile par apparence de bloc, dessinée par quelques règles simples.
    internal static class BlockAtlas
    {
        public const int TileCount = 7;

        private const int TileSize = 16;
        private const int StoneTile = 0;
        private const int DirtTile = 1;
        private const int GrassTopTile = 2;
        private const int GrassSideTile = 3;
        private const int PlanksTile = 4;
        private const int BricksTile = 5;
        private const int TimberedTile = 6;

        private const int GrassSideBand = 4;
        private const int PlankWidth = 4;
        private const int BrickHeight = 4;
        private const int BrickLength = 8;

        private static readonly Color Stone = new Color(0.50f, 0.50f, 0.52f);
        private static readonly Color Dirt = new Color(0.47f, 0.33f, 0.20f);
        private static readonly Color Grass = new Color(0.33f, 0.60f, 0.25f);
        private static readonly Color Plank = new Color(0.72f, 0.55f, 0.33f);
        private static readonly Color PlankSeam = new Color(0.50f, 0.36f, 0.20f);
        private static readonly Color Brick = new Color(0.62f, 0.27f, 0.20f);
        private static readonly Color Mortar = new Color(0.76f, 0.73f, 0.68f);
        private static readonly Color Plaster = new Color(0.88f, 0.85f, 0.76f);
        private static readonly Color Beam = new Color(0.33f, 0.22f, 0.12f);

        public static int TileFor(ushort block, int face)
        {
            switch (Blocks.TypeOf(block))
            {
                case Blocks.Dirt:
                    return DirtTile;
                case Blocks.Grass:
                    if (face == ChunkMesher.TopFace) return GrassTopTile;
                    return face == ChunkMesher.BottomFace ? DirtTile : GrassSideTile;
                case Blocks.Planks:
                    return PlanksTile;
                case Blocks.Bricks:
                    return BricksTile;
                case Blocks.Timbered:
                    return TimberedTile;
                default:
                    return StoneTile;
            }
        }

        public static Texture2D CreateTexture()
        {
            var texture = new Texture2D(TileCount * TileSize, TileSize, TextureFormat.RGBA32, false)
            {
                name = "BlockAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[TileCount * TileSize * TileSize];
            for (int tile = 0; tile < TileCount; tile++)
            {
                for (int y = 0; y < TileSize; y++)
                {
                    for (int x = 0; x < TileSize; x++)
                    {
                        Color color = BaseColor(tile, x, y) * (0.9f + 0.1f * Noise(tile, x, y));
                        color.a = 1f;
                        pixels[y * TileCount * TileSize + tile * TileSize + x] = color;
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Color BaseColor(int tile, int x, int y)
        {
            switch (tile)
            {
                case DirtTile:
                    return Dirt;
                case GrassTopTile:
                    return Grass;
                case GrassSideTile:
                    return y >= TileSize - GrassSideBand ? Grass : Dirt;
                case PlanksTile:
                    return y % PlankWidth == 0 ? PlankSeam : Plank;
                case BricksTile:
                    return IsMortar(x, y) ? Mortar : Brick;
                case TimberedTile:
                    return IsBeam(x, y) ? Beam : Plaster;
                default:
                    return Stone;
            }
        }

        // Des rangées de briques décalées d'une demi-brique une rangée sur deux.
        private static bool IsMortar(int x, int y)
        {
            if (y % BrickHeight == 0) return true;

            int offset = y / BrickHeight % 2 == 0 ? 0 : BrickLength / 2;
            return (x + offset) % BrickLength == 0;
        }

        // Un cadre de poutres et une diagonale sur un fond d'enduit.
        private static bool IsBeam(int x, int y)
        {
            bool frame = x == 0 || y == 0 || x == TileSize - 1 || y == TileSize - 1;
            return frame || Mathf.Abs(x - y) <= 1;
        }

        private static float Noise(int tile, int x, int y)
        {
            int hash = (x * 73856093) ^ (y * 19349663) ^ (tile * 83492791);
            return (hash & 0xFF) / 255f;
        }
    }
}
