using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Voxel.Client;
using Voxel.Core;
using static Voxel.Client.BlockAtlas;

namespace Voxel.EditorTools
{
    // The wood blocks' tiles, three per wood (WoodBlocks.Species), in the colours of its tree's asset:
    // planks (four boards across, their joints staggered, grain along them, a nail by each joint) in
    // its wood colour; a log's bark (furrows running up it) in its bark's colour; a log's end (growth
    // rings round a dark pith, a rim of bark) in its wood colour. Patterns wrap around the tile.
    internal static partial class BlockTextureBuilder
    {
        private const int BoardHeight = TileSize / 4;

        private static readonly Dictionary<int, (Color wood, Color bark)> WoodColours = new Dictionary<int, (Color, Color)>();

        private static Color WoodColour(int tile, int x, int y)
        {
            int wood = (tile - FirstPlanksTile) % WoodBlocks.Count;
            int kind = (tile - FirstPlanksTile) / WoodBlocks.Count;
            (Color woodColour, Color barkColour) = ColoursOf(wood);
            switch (kind)
            {
                case 0: return PlanksColour(tile, x, y, woodColour);
                case 1: return BarkColour(tile, x, y, barkColour);
                default: return RingsColour(tile, x, y, woodColour, barkColour);
            }
        }

        // A wood's colour and its bark's, read from its species asset (the bark material's base colour).
        private static (Color wood, Color bark) ColoursOf(int wood)
        {
            if (WoodColours.TryGetValue(wood, out (Color, Color) known)) return known;

            var asset = AssetDatabase.LoadAssetAtPath<SpeciesAsset>($"Assets/Species/{WoodBlocks.Species[wood].Asset}.asset");
            Color woodColour = asset != null ? asset.WoodColour : new Color(0.72f, 0.53f, 0.31f);
            Material bark = asset != null ? asset.BarkMaterial : null;
            Color barkColour = bark != null && bark.HasProperty("_BaseColor") ? bark.GetColor("_BaseColor") : woodColour * 0.55f;
            barkColour.a = 1f;
            woodColour.a = 1f;
            WoodColours[wood] = (woodColour, barkColour);
            return (woodColour, barkColour);
        }

        private static Color PlanksColour(int tile, int x, int y, Color wood)
        {
            int board = y / BoardHeight;
            int inBoard = y % BoardHeight;

            // The gap under each board, and its top edge a little lighter.
            if (inBoard == 0) return wood * 0.5f;
            if (inBoard == BoardHeight - 1) return wood * 1.06f;

            // Each board is cut in two by a joint, staggered from one board to the next.
            int joint = (board * 23 + 11) % TileSize;
            if (x == joint) return wood * 0.55f;

            // A nail on each side of the joint.
            bool nail = (inBoard == 4 || inBoard == BoardHeight - 5) && (x == (joint + 3) % TileSize || x == (joint + TileSize - 3) % TileSize);
            if (nail) return wood * 0.4f;

            // Grain: long streaks along the board, each board a shade of its own.
            float shade = 0.92f + 0.12f * Noise(tile + 900, board, 0);
            float grain = Noise(tile + 910, x / 9 + board * 7, y);
            float streak = grain < 0.18f ? 0.88f : grain > 0.85f ? 1.06f : 1f;
            float fleck = Noise(tile + 920, x, y) > 0.96f ? 0.9f : 1f;
            return wood * shade * streak * fleck;
        }

        private static Color BarkColour(int tile, int x, int y, Color bark)
        {
            // Furrows running up the log, wandering a little, and plates of bark between them.
            int wander = (int)(SmoothNoise(tile + 930, x, y, 16) * 4f);
            float furrow = Noise(tile + 940, (x + wander) / 3, y / 8);
            float plate = Noise(tile + 950, (x + wander) / 5, y / 13);
            float shade = furrow < 0.22f ? 0.62f : furrow > 0.86f ? 1.15f : 1f;
            shade *= 0.94f + 0.12f * plate;
            if (Noise(tile + 960, x, y) > 0.95f) shade *= 0.85f;
            return bark * shade;
        }

        private static Color RingsColour(int tile, int x, int y, Color wood, Color bark)
        {
            float dx = x - 31.5f;
            float dy = y - 31.5f;
            float radius = Mathf.Sqrt(dx * dx + dy * dy);

            // A rim of bark, the corners too, a little ragged.
            float rim = 28.5f + 1.5f * SmoothNoise(tile + 970, x, y, 8);
            if (radius > rim) return bark * (0.9f + 0.15f * Noise(tile + 980, x, y));
            if (radius < 1.6f) return wood * 0.55f;

            // Rings: lighter early wood, darker late wood, about every three pixels, slightly wavy.
            float wave = radius + 1.2f * SmoothNoise(tile + 990, x, y, 16);
            float ring = Mathf.Repeat(wave, 3.2f);
            float shade = ring < 0.9f ? 0.8f : 1f;
            shade *= 0.97f + 0.06f * Noise(tile + 1000, x, y);

            // The sapwood near the bark is paler.
            if (radius > rim - 4f) shade *= 1.08f;
            return wood * shade;
        }
    }
}
