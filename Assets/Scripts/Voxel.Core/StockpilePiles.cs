using System;
using System.IO;

namespace Voxel.Core
{
    // The piles of a stockpile, one per cell (StorageRules.PileCell): each holds one block type (or
    // wood), up to PileSize (StorageRules.PileSizeOf its kind), and takes another type only once empty.
    public sealed class StockpilePiles
    {
        private readonly int[] items;
        private readonly int[] counts;

        // A large stockpile's piles.
        public StockpilePiles() : this(StorageRules.PileCount, StorageRules.PileSize)
        {
        }

        public StockpilePiles(int pileCount, int pileSize)
        {
            items = new int[pileCount];
            counts = new int[pileCount];
            PileSize = pileSize;
        }

        public StockpilePiles(StorageKind kind) : this(StorageRules.PileCountOf(kind), StorageRules.PileSizeOf(kind))
        {
        }

        public int Count => items.Length;

        public int PileSize { get; }

        public int Capacity => Count * PileSize;

        public int ItemAt(int pile) => items[pile];

        public int CountAt(int pile) => counts[pile];

        public int Total
        {
            get
            {
                int total = 0;
                foreach (int count in counts)
                {
                    total += count;
                }

                return total;
            }
        }

        public bool IsEmpty => Total == 0;

        public int CountOf(int item)
        {
            int total = 0;
            for (int pile = 0; pile < items.Length; pile++)
            {
                if (items[pile] == item) total += counts[pile];
            }

            return total;
        }

        // How many more of an item fit: the room left on its piles, then the empty ones.
        public int Room(int item)
        {
            if (!StorageRules.AcceptsInStockpile(item)) return 0;

            int room = 0;
            for (int pile = 0; pile < items.Length; pile++)
            {
                if (counts[pile] == 0) room += PileSize;
                else if (items[pile] == item) room += PileSize - counts[pile];
            }

            return room;
        }

        // Adds what fits, on the piles of that item first, then on empty ones in order. Returns how many.
        public int Add(int item, int count)
        {
            if (count <= 0 || !StorageRules.AcceptsInStockpile(item)) return 0;

            int added = 0;
            for (int pass = 0; pass < 2 && added < count; pass++)
            {
                for (int pile = 0; pile < items.Length && added < count; pile++)
                {
                    bool fits = pass == 0 ? counts[pile] > 0 && items[pile] == item : counts[pile] == 0;
                    if (!fits) continue;

                    int put = Math.Min(count - added, PileSize - counts[pile]);
                    items[pile] = item;
                    counts[pile] += put;
                    added += put;
                }
            }

            return added;
        }

        // Takes up to count from a pile. Returns how many.
        public int Take(int pile, int count)
        {
            if ((uint)pile >= (uint)items.Length || count <= 0) return 0;

            int taken = Math.Min(count, counts[pile]);
            counts[pile] -= taken;
            if (counts[pile] == 0) items[pile] = Items.None;
            return taken;
        }

        // Sets a pile as another machine says it is (a client's copy). False, changing nothing, if
        // that cannot be a pile.
        public bool Set(int pile, int item, int count)
        {
            if ((uint)pile >= (uint)items.Length || count < 0 || count > PileSize) return false;
            if (count > 0 && !StorageRules.AcceptsInStockpile(item)) return false;

            items[pile] = count > 0 ? item : Items.None;
            counts[pile] = count;
            return true;
        }

        public void CopyFrom(StockpilePiles other)
        {
            Array.Copy(other.items, items, items.Length);
            Array.Copy(other.counts, counts, counts.Length);
        }

        public void Write(BinaryWriter writer)
        {
            for (int pile = 0; pile < items.Length; pile++)
            {
                writer.Write(items[pile]);
                writer.Write(counts[pile]);
            }
        }

        // A pile of an item that no longer exists, or a count out of range, is left empty.
        public void Read(BinaryReader reader)
        {
            for (int pile = 0; pile < items.Length; pile++)
            {
                int item = Items.Upgrade(reader.ReadInt32());
                int count = reader.ReadInt32();
                bool valid = count > 0 && count <= PileSize && StorageRules.AcceptsInStockpile(item);
                items[pile] = valid ? item : Items.None;
                counts[pile] = valid ? count : 0;
            }
        }
    }
}
