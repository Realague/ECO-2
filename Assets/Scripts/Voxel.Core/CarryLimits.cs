using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // What a player can carry: at most 40 blocks, all types together (wood counted as blocks, though
    // it cannot be placed), and at most 30 kg of everything else (straw, seeds, crops, tools). An
    // inventory already beyond a limit (a save from before it) is kept as it is: it only cannot take
    // more of that kind until it is back under.
    public static class CarryLimits
    {
        public const int MaxBlocks = 40;
        public const float MaxWeight = 30f;

        // What counts against the blocks' limit rather than the weight: blocks, and every wood. A torch
        // is a block type but weighs, like a tool.
        public static bool CountsAsBlock(int item) => (Items.IsBlock(item) && item != Blocks.Torch) || Items.IsWood(item);

        public static int BlockCount(Inventory inventory)
        {
            int count = 0;
            foreach (KeyValuePair<int, int> entry in inventory.Entries)
            {
                if (CountsAsBlock(entry.Key)) count += entry.Value;
            }

            return count;
        }

        public static float Weight(Inventory inventory, ItemWeights weights)
        {
            float weight = 0f;
            foreach (KeyValuePair<int, int> entry in inventory.Entries)
            {
                if (!CountsAsBlock(entry.Key)) weight += weights.Of(entry.Key) * entry.Value;
            }

            return weight;
        }

        // How many more of an item the inventory can take.
        public static int Room(Inventory inventory, int item, ItemWeights weights)
        {
            if (CountsAsBlock(item)) return Math.Max(0, MaxBlocks - BlockCount(inventory));

            float each = weights.Of(item);
            if (each <= 0f) return int.MaxValue;

            // A hair of slack, so that float sums (0.1 + 0.2...) do not refuse what exactly fits.
            float free = MaxWeight - Weight(inventory, weights) + 1e-4f;
            return free <= 0f ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(free / each));
        }
    }
}
