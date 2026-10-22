using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Voxel.Client;
using static Voxel.Client.BlockAtlas;

namespace Voxel.EditorTools
{
    // Paints the starting block textures (menu Eco > Write missing block textures): one 64 × 64 PNG per
    // tile of BlockAtlas.TileNames in Assets/Resources/Blocks, plus its tint mask for grass, drawn by a
    // few simple rules: pixel noise gives soils their grain; rocks are drawn like pixel art
    // (BlockTextureBuilder.Rocks). Only missing files are written, so files edited by hand are never
    // overwritten: delete one to get its painted tile back.
    internal static partial class BlockTextureBuilder
    {
        private const string Folder = "Assets/Resources/" + ResourceFolder;

        private const int TileSize = 64;

        // Depth of the green band at the top of a grass block's sides, in pixels, give or take its ragged edge.
        private const int GrassSideBand = 16;

        private static readonly Color Dirt = new Color(0.47f, 0.33f, 0.20f);

        // Tinted pixels are drawn light grey: the climate's colour, multiplied in by the shader, makes their colour.
        private static readonly Color TintBase = new Color(0.97f, 0.97f, 0.97f);
        private static readonly Color Ice = new Color(0.70f, 0.85f, 0.95f);
        private static readonly Color IceCrack = new Color(0.86f, 0.94f, 1.00f);
        private static readonly Color Sand = new Color(0.88f, 0.80f, 0.56f);
        private static readonly Color Clay = new Color(0.62f, 0.59f, 0.57f);

        [MenuItem("Eco/Write missing block textures")]
        private static void WriteMissing()
        {
            Directory.CreateDirectory(Folder);
            var written = new List<string>();
            for (int tile = 0; tile < TileCount; tile++)
            {
                string name = TileNames[tile];
                string path = $"{Folder}/{name}.png";
                if (!File.Exists(path))
                {
                    WritePng(path, (x, y) => TileColour(tile, x, y));
                    written.Add(name);
                }

                string maskPath = $"{Folder}/{name}{TintSuffix}.png";
                if (HasTint(tile) && !File.Exists(maskPath))
                {
                    WritePng(maskPath, (x, y) => IsTinted(tile, x, y) ? Color.white : Color.black);
                    written.Add(name + TintSuffix);
                }
            }

            AssetDatabase.Refresh();
            Debug.Log(written.Count == 0
                ? $"Every block texture is already in {Folder}."
                : $"Wrote {written.Count} block textures to {Folder}: {string.Join(", ", written)}.");
        }

        private delegate Color Pixel(int x, int y);

        private static void WritePng(string path, Pixel pixel)
        {
            var texture = new Texture2D(TileSize, TileSize, TextureFormat.RGBA32, false);
            for (int y = 0; y < TileSize; y++)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    Color colour = pixel(x, y);
                    colour.a = 1f;
                    texture.SetPixel(x, y, colour);
                }
            }

            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
        }

        private static Color TileColour(int tile, int x, int y)
        {
            return IsPainted(tile) ? PaintedColour(tile, x, y) : BaseColor(tile, x, y) * (0.9f + 0.1f * Noise(tile, x, y));
        }

        // Soils and ice; rocks and snow are in BlockTextureBuilder.Rocks. Repeating patterns have periods that
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
                case TilledSoilTopTile:
                    // Furrows: eight ridges across the block, lit on their upper side, dark and moist in
                    // the hollow between, with the dirt's crumbs over all.
                    int row = y % 8;
                    float shade = row < 2 ? 0.72f : row < 4 ? 1.12f : row < 6 ? 0.98f : 0.85f;
                    return DirtAt(crumb, pebble) * shade * 0.92f;
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

        private static bool HasTint(int tile) => tile == GrassTopTile || tile == GrassSideTile;

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

    // Import settings of the block textures, whatever file is dropped there: BlockAtlas reads their
    // pixels (readable), at full precision (uncompressed), and makes the mipmaps itself, tile by tile.
    internal sealed class BlockTextureImport : AssetPostprocessor
    {
        private void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/Resources/" + ResourceFolder + "/")) return;

            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.Default;
            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaIsTransparency = false;
            importer.sRGBTexture = !Path.GetFileNameWithoutExtension(assetPath).EndsWith(TintSuffix);
        }
    }
}
