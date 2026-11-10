using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Voxel.Client
{
    // Weighted random block textures, as OptiFine's random textures: a tile of BlockAtlas (Granite.png)
    // may have variants, files named after it with a number (Granite_2.png, Granite_3.png… up to
    // MaxVariants), which take places of their own in the atlas after the base tiles (its "slots"). Each
    // face of a block picks one from a hash of its position and side, the same on every machine (display
    // only: nothing is sent or saved), weighted by the weights file (Resources/Blocks/Variants.txt,
    // "Granite: 4 2 1" for Granite.png, _2 and _3; equal without a line). Tiles listed on its "rotate:"
    // line also turn by a quarter at random, and may be mirrored, on the top and bottom of a block (never
    // on its sides, where the grass band or the beds of a rock must stay level).
    public sealed class TileVariants
    {
        public const int MaxVariants = 16;
        public const string WeightsFile = "Variants";
        private const string RotateKey = "rotate";

        private readonly string[] slotNames;
        private readonly int[] baseOfSlot;
        private readonly int[][] slots;
        private readonly int[][] cumulative;
        private readonly bool[] rotates;

        // Every base tile alone, none turned: what the meshes show before the atlas is made (and in tests).
        public static readonly TileVariants None = Single(BlockAtlas.TileNames);

        // These tiles alone, none turned.
        public static TileVariants Single(string[] tileNames) =>
            new TileVariants(tileNames, new Dictionary<int, int>(), new Dictionary<string, int[]>(), new HashSet<string>());

        private TileVariants(string[] tileNames, Dictionary<int, int> variantCounts, Dictionary<string, int[]> weights, HashSet<string> rotated)
        {
            int tileCount = tileNames.Length;
            var names = new List<string>(tileNames);
            var bases = new List<int>();
            for (int tile = 0; tile < tileCount; tile++) bases.Add(tile);

            slots = new int[tileCount][];
            cumulative = new int[tileCount][];
            rotates = new bool[tileCount];
            for (int tile = 0; tile < tileCount; tile++)
            {
                int count = 1 + (variantCounts.TryGetValue(tile, out int extra) ? extra : 0);
                slots[tile] = new int[count];
                slots[tile][0] = tile;
                for (int v = 1; v < count; v++)
                {
                    slots[tile][v] = names.Count;
                    names.Add(VariantName(tileNames[tile], v + 1));
                    bases.Add(tile);
                }

                weights.TryGetValue(tileNames[tile], out int[] given);
                cumulative[tile] = new int[count];
                int total = 0;
                for (int v = 0; v < count; v++)
                {
                    total += given != null && v < given.Length ? given[v] : 1;
                    cumulative[tile][v] = total;
                }

                rotates[tile] = rotated.Contains(tileNames[tile]);
            }

            slotNames = names.ToArray();
            baseOfSlot = bases.ToArray();
        }

        // How many places the atlas has: the base tiles, then every variant.
        public int SlotCount => slotNames.Length;

        // The file of a slot, without its extension (Granite, Granite_2…).
        public string SlotName(int slot) => slotNames[slot];

        // The base tile a slot is a variant of (itself for a base tile).
        public int BaseOf(int slot) => baseOfSlot[slot];

        public int VariantCount(int tile) => slots[tile].Length;

        public bool Rotates(int tile) => rotates[tile];

        public static string VariantName(string tileName, int number) => $"{tileName}_{number}";

        // The slot a face of the block at (x, y, z) shows for this base tile, and how its texture turns:
        // quarter turns and a mirror, only on the top and bottom of a tile that rotates, or on every
        // face (everyFace: an ore's crystals, which have no way up).
        public int Pick(int tile, int x, int y, int z, int face, out int turn, out bool mirror, bool everyFace = false)
        {
            uint hash = Hash(x, y, z, face);
            turn = 0;
            mirror = false;
            if (rotates[tile] && (everyFace || face == ChunkMesher.TopFace || face == ChunkMesher.BottomFace))
            {
                turn = (int)(hash >> 24) & 3;
                mirror = ((hash >> 26) & 1) != 0;
            }

            int[] weights = cumulative[tile];
            int total = weights[weights.Length - 1];
            if (weights.Length == 1 || total <= 0) return slots[tile][0];

            int roll = (int)((hash & 0xFFFFFF) % (uint)total);
            for (int v = 0; v < weights.Length; v++)
            {
                if (roll < weights[v]) return slots[tile][v];
            }

            return slots[tile][0];
        }

        // An integer hash of a block face, the same on every machine.
        private static uint Hash(int x, int y, int z, int face)
        {
            uint h = unchecked((uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)z * 0xCB1AB31Fu ^ (uint)(face + 1) * 0x165667B1u);
            h ^= h >> 15;
            h = unchecked(h * 0x2C1B3C6Du);
            h ^= h >> 12;
            h = unchecked(h * 0x297A2D39u);
            h ^= h >> 15;
            return h;
        }

        // The variants found in Resources/Blocks and their weights (main thread: Resources).
        public static TileVariants FromResources() => FromResources(BlockAtlas.ResourceFolder, BlockAtlas.TileNames);

        // The variants of these tiles found in a folder under Resources, with the weights file there if any.
        public static TileVariants FromResources(string folder, string[] tileNames)
        {
            var weightsAsset = Resources.Load<TextAsset>($"{folder}/{WeightsFile}");
            string text = weightsAsset != null ? weightsAsset.text : "";
            if (weightsAsset != null) Resources.UnloadAsset(weightsAsset);

            var problems = new List<string>();
            TileVariants variants = Parse(tileNames, name =>
            {
                var texture = Resources.Load<Texture2D>($"{folder}/{name}");
                if (texture == null) return false;

                Resources.UnloadAsset(texture);
                return true;
            }, text, problems);

            foreach (string problem in problems)
            {
                Debug.LogWarning($"Block texture variants (Resources/{folder}/{WeightsFile}.txt): {problem}");
            }

            return variants;
        }

        // The variants of these tiles: each tile's files numbered from 2 while they exist, and the
        // weights file's lines ("Name: w1 w2 …", the base file's weight first; "rotate: Name Name …";
        // "#" starts a comment). What cannot be read is reported in problems and left out.
        public static TileVariants Parse(string[] tileNames, Func<string, bool> fileExists, string weightsText, List<string> problems)
        {
            var known = new Dictionary<string, int>();
            for (int tile = 0; tile < tileNames.Length; tile++) known[tileNames[tile]] = tile;

            var counts = new Dictionary<int, int>();
            for (int tile = 0; tile < tileNames.Length; tile++)
            {
                int extra = 0;
                while (extra + 2 <= MaxVariants && fileExists(VariantName(tileNames[tile], extra + 2))) extra++;
                if (extra > 0) counts[tile] = extra;
            }

            var weights = new Dictionary<string, int[]>();
            var rotated = new HashSet<string>();
            string[] lines = (weightsText ?? "").Split('\n');
            for (int number = 0; number < lines.Length; number++)
            {
                string line = lines[number];
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0) continue;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    problems.Add($"line {number + 1}: expected \"Name: weights\".");
                    continue;
                }

                string key = line.Substring(0, colon).Trim();
                string[] values = line.Substring(colon + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (key == RotateKey)
                {
                    foreach (string name in values)
                    {
                        if (known.ContainsKey(name)) rotated.Add(name);
                        else problems.Add($"line {number + 1}: no tile named {name}.");
                    }

                    continue;
                }

                if (!known.TryGetValue(key, out int tileIndex))
                {
                    problems.Add($"line {number + 1}: no tile named {key}.");
                    continue;
                }

                int files = 1 + (counts.TryGetValue(tileIndex, out int found) ? found : 0);
                var parsed = new List<int>();
                foreach (string value in values)
                {
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int weight)) parsed.Add(weight);
                    else problems.Add($"line {number + 1}: \"{value}\" is not a whole number.");
                }

                if (parsed.Count > files) problems.Add($"line {number + 1}: {parsed.Count} weights for {files} files of {key}; the extra ones are ignored.");
                weights[key] = parsed.ToArray();
            }

            return new TileVariants(tileNames, counts, weights, rotated);
        }
    }
}
