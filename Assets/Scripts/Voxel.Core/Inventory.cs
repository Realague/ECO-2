using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // The items a player owns: a count per item type, with no capacity limit.
    public sealed class Inventory
    {
        // Sorted by item number: the display order does not change when counts change.
        private readonly SortedDictionary<int, int> counts = new SortedDictionary<int, int>();

        public int DistinctCount => counts.Count;

        public IEnumerable<KeyValuePair<int, int>> Entries => counts;

        public int Count(int item) => counts.TryGetValue(item, out int count) ? count : 0;

        public bool Has(int item, int count = 1) => Count(item) >= count;

        public void Add(int item, int count)
        {
            if (item == Items.None || count <= 0) return;

            counts[item] = Count(item) + count;
        }

        // Removes the requested count, or nothing at all if the player does not have enough.
        public bool TryRemove(int item, int count = 1)
        {
            if (count <= 0 || !Has(item, count)) return false;

            int left = Count(item) - count;
            if (left > 0) counts[item] = left;
            else counts.Remove(item);
            return true;
        }

        public void Clear() => counts.Clear();

        public void CopyFrom(Inventory other)
        {
            counts.Clear();
            foreach (KeyValuePair<int, int> entry in other.counts)
            {
                counts.Add(entry.Key, entry.Value);
            }
        }

        // Moves items from one inventory to another. Returns false, changing nothing, if the
        // first does not have enough.
        public static bool Transfer(Inventory from, Inventory to, int item, int count)
        {
            if (!from.TryRemove(item, count)) return false;

            to.Add(item, count);
            return true;
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(counts.Count);
            foreach (KeyValuePair<int, int> entry in counts)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value);
            }
        }

        // The data may come from a damaged file: any unknown item or invalid count is refused.
        public void Read(BinaryReader reader)
        {
            counts.Clear();
            int entries = reader.ReadInt32();
            for (int i = 0; i < entries; i++)
            {
                int item = reader.ReadInt32();
                int count = reader.ReadInt32();
                if (Items.IsRetired(item)) continue;

                if (!Items.IsValid(item) || count <= 0 || counts.ContainsKey(item))
                {
                    throw new InvalidDataException($"Invalid inventory: item {item}, count {count}.");
                }

                counts.Add(item, count);
            }
        }
    }
}
