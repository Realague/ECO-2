using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // Storage of one kind that shares what it holds: the chests, or the stockpiles, of one claimed
    // zone (Claims.ZoneOf), in a fixed order. A pool of chests is one chest of their summed weight; a
    // pool of stockpiles (large and small together) is all their piles one after another, each
    // stockpile's after the last one's. Items go into the members in order and are taken from them in order.
    public static class StoragePool
    {
        // ---- Chests ----

        public static int Count(IReadOnlyList<Storage> chests, int item)
        {
            long count = 0;
            foreach (Storage chest in chests)
            {
                count += chest.Contents.Count(item);
            }

            return (int)Math.Min(int.MaxValue, count);
        }

        public static float Weight(IReadOnlyList<Storage> chests, ItemWeights weights)
        {
            float weight = 0f;
            foreach (Storage chest in chests)
            {
                weight += StorageRules.ChestWeight(chest.Contents, weights);
            }

            return weight;
        }

        // What the chests hold together.
        public static Inventory Contents(IReadOnlyList<Storage> chests)
        {
            var contents = new Inventory();
            foreach (Storage chest in chests)
            {
                foreach (KeyValuePair<int, int> entry in chest.Contents.Entries)
                {
                    contents.Add(entry.Key, entry.Value);
                }
            }

            return contents;
        }

        // How many more of an item the chests can take together, each by its own weight.
        public static int ChestRoom(IReadOnlyList<Storage> chests, int item, ItemWeights weights)
        {
            long room = 0;
            foreach (Storage chest in chests)
            {
                room += StorageRules.ChestRoom(chest.Contents, item, weights);
            }

            return (int)Math.Min(int.MaxValue, room);
        }

        // Whether the chests have room for all of these together (what a station makes at once), each
        // put in order as AddToChests would.
        public static bool ChestRoomFor(IReadOnlyList<Storage> chests, IReadOnlyList<(int Item, int Count)> items, ItemWeights weights)
        {
            var copies = new Inventory[chests.Count];
            for (int i = 0; i < copies.Length; i++)
            {
                copies[i] = new Inventory();
                copies[i].CopyFrom(chests[i].Contents);
            }

            foreach ((int item, int count) in items)
            {
                int left = count;
                foreach (Inventory copy in copies)
                {
                    int put = Math.Min(left, StorageRules.ChestRoom(copy, item, weights));
                    if (put <= 0) continue;

                    copy.Add(item, put);
                    left -= put;
                    if (left == 0) break;
                }

                if (left > 0) return false;
            }

            return true;
        }

        // Puts what fits into the chests in order. Returns how many.
        public static int AddToChests(IReadOnlyList<Storage> chests, int item, int count, ItemWeights weights)
        {
            int added = 0;
            foreach (Storage chest in chests)
            {
                if (added >= count) break;

                int put = Math.Min(count - added, StorageRules.ChestRoom(chest.Contents, item, weights));
                if (put <= 0) continue;

                chest.Contents.Add(item, put);
                added += put;
            }

            return added;
        }

        // Takes up to count of an item from the chests in order. Returns how many.
        public static int TakeFromChests(IReadOnlyList<Storage> chests, int item, int count)
        {
            int taken = 0;
            foreach (Storage chest in chests)
            {
                if (taken >= count) break;

                int take = Math.Min(count - taken, chest.Contents.Count(item));
                if (take <= 0) continue;

                chest.Contents.TryRemove(item, take);
                taken += take;
            }

            return taken;
        }

        // ---- Stockpiles ----

        public static int PileCount(IReadOnlyList<Storage> stockpiles)
        {
            int count = 0;
            foreach (Storage stockpile in stockpiles)
            {
                count += stockpile.Piles.Count;
            }

            return count;
        }

        // How many blocks the stockpiles hold together at most.
        public static int Capacity(IReadOnlyList<Storage> stockpiles)
        {
            int capacity = 0;
            foreach (Storage stockpile in stockpiles)
            {
                capacity += stockpile.Piles.Capacity;
            }

            return capacity;
        }

        // The stockpile and its pile behind a pile of the pool.
        public static bool TryFindPile(IReadOnlyList<Storage> stockpiles, int pile, out Storage stockpile, out int index)
        {
            stockpile = null;
            index = -1;
            if (pile < 0) return false;

            foreach (Storage member in stockpiles)
            {
                if (pile < member.Piles.Count)
                {
                    stockpile = member;
                    index = pile;
                    return true;
                }

                pile -= member.Piles.Count;
            }

            return false;
        }

        public static int StockpileRoom(IReadOnlyList<Storage> stockpiles, int item)
        {
            long room = 0;
            foreach (Storage stockpile in stockpiles)
            {
                room += stockpile.Piles.Room(item);
            }

            return (int)Math.Min(int.MaxValue, room);
        }

        // Puts what fits onto the stockpiles in order. Returns how many.
        public static int AddToStockpiles(IReadOnlyList<Storage> stockpiles, int item, int count)
        {
            int added = 0;
            foreach (Storage stockpile in stockpiles)
            {
                if (added >= count) break;

                added += stockpile.Piles.Add(item, count - added);
            }

            return added;
        }

        // How many of an item the stockpiles hold together.
        public static int StockpileCount(IReadOnlyList<Storage> stockpiles, int item)
        {
            int count = 0;
            foreach (Storage stockpile in stockpiles)
            {
                count += stockpile.Piles.CountOf(item);
            }

            return count;
        }

        // Takes up to count of an item from the piles holding it, in the pool's order. Returns how many.
        public static int TakeFromStockpiles(IReadOnlyList<Storage> stockpiles, int item, int count)
        {
            int taken = 0;
            foreach (Storage stockpile in stockpiles)
            {
                for (int pile = 0; pile < stockpile.Piles.Count && taken < count; pile++)
                {
                    if (stockpile.Piles.ItemAt(pile) == item) taken += stockpile.Piles.Take(pile, count - taken);
                }
            }

            return taken;
        }

        // Whether the stockpiles have room for all of these together (what a station makes at once),
        // each put in order as AddToStockpiles would: checked on copies, since kinds of blocks share
        // the empty piles.
        public static bool StockpileRoomFor(IReadOnlyList<Storage> stockpiles, IReadOnlyList<(int Item, int Count)> items)
        {
            var copies = new List<Storage>();
            foreach (Storage stockpile in stockpiles)
            {
                var copy = new Storage(stockpile.Id, stockpile.Kind, stockpile.Position, stockpile.Facing, stockpile.OwnerId);
                copy.Piles.CopyFrom(stockpile.Piles);
                copies.Add(copy);
            }

            foreach ((int item, int count) in items)
            {
                if (AddToStockpiles(copies, item, count) < count) return false;
            }

            return true;
        }

        // ---- Both ----

        // Moves everything a storage holds into the others of its pool, if all of it fits; else
        // changes nothing. What lets a storage be picked up while the rest of its pool keeps its things.
        public static bool TryEmptyInto(Storage storage, IReadOnlyList<Storage> others, ItemWeights weights)
        {
            if (storage.IsEmpty) return true;

            if (storage.Kind == StorageKind.Chest)
            {
                // All of it at once by weight: the room by item would count the same weight twice.
                float free = 0f;
                foreach (Storage other in others)
                {
                    free += StorageRules.ChestMaxWeight - StorageRules.ChestWeight(other.Contents, weights);
                }

                if (StorageRules.ChestWeight(storage.Contents, weights) > free + 1e-4f) return false;

                var entries = new List<KeyValuePair<int, int>>(storage.Contents.Entries);
                var moved = new List<KeyValuePair<int, int>>();
                foreach (KeyValuePair<int, int> entry in entries)
                {
                    int added = AddToChests(others, entry.Key, entry.Value, weights);
                    moved.Add(new KeyValuePair<int, int>(entry.Key, added));
                    if (added == entry.Value) continue;

                    // Did not fit after all (the weights split badly across chests): put it all back.
                    foreach (KeyValuePair<int, int> back in moved)
                    {
                        TakeFromChests(others, back.Key, back.Value);
                    }

                    return false;
                }

                storage.Contents.Clear();
                return true;
            }

            var room = new Dictionary<int, int>();
            for (int pile = 0; pile < storage.Piles.Count; pile++)
            {
                int item = storage.Piles.ItemAt(pile);
                int count = storage.Piles.CountAt(pile);
                if (count == 0) continue;

                room.TryGetValue(item, out int needed);
                room[item] = needed + count;
            }

            // Each kind of block takes its own piles, so check them together on copies.
            var copies = new List<Storage>();
            foreach (Storage other in others)
            {
                var copy = new Storage(other.Id, other.Kind, other.Position, other.Facing, other.OwnerId);
                copy.Piles.CopyFrom(other.Piles);
                copies.Add(copy);
            }

            foreach (KeyValuePair<int, int> need in room)
            {
                if (AddToStockpiles(copies, need.Key, need.Value) < need.Value) return false;
            }

            for (int i = 0; i < others.Count; i++)
            {
                others[i].Piles.CopyFrom(copies[i].Piles);
            }

            for (int pile = 0; pile < storage.Piles.Count; pile++)
            {
                storage.Piles.Take(pile, storage.Piles.PileSize);
            }

            return true;
        }
    }
}
