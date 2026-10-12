using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Atlas provisoire généré au lancement : une tuile de couleur par apparence de bloc.
    internal static class BlockAtlas
    {
        public const int TileCount = 4;

        private const int TileSize = 16;
        private const int StoneTile = 0;
        private const int DirtTile = 1;
        private const int GrassTopTile = 2;
        private const int GrassSideTile = 3;
        private const int GrassSideBand = 4;

        private static readonly Color Stone = new Color(0.50f, 0.50f, 0.52f);
        private static readonly Color Dirt = new Color(0.47f, 0.33f, 0.20f);
        private static readonly Color Grass = new Color(0.33f, 0.60f, 0.25f);

        public static int TileFor(ushort block, int face)
        {
            switch (Blocks.TypeOf(block))
            {
                case Blocks.Dirt:
                    return DirtTile;
                case Blocks.Grass:
                    if (face == ChunkMesher.TopFace) return GrassTopTile;
                    return face == ChunkMesher.BottomFace ? DirtTile : GrassSideTile;
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
                        Color color = BaseColor(tile, y) * (0.9f + 0.1f * Noise(tile, x, y));
                        color.a = 1f;
                        pixels[y * TileCount * TileSize + tile * TileSize + x] = color;
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Color BaseColor(int tile, int y)
        {
            switch (tile)
            {
                case DirtTile:
                    return Dirt;
                case GrassTopTile:
                    return Grass;
                case GrassSideTile:
                    return y >= TileSize - GrassSideBand ? Grass : Dirt;
                default:
                    return Stone;
            }
        }

        private static float Noise(int tile, int x, int y)
        {
            int hash = (x * 73856093) ^ (y * 19349663) ^ (tile * 83492791);
            return (hash & 0xFF) / 255f;
        }
    }
}
