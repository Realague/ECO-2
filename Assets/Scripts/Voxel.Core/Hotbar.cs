using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // The ten quick slots of a player, each holding an item type (or nothing): the player picks the
    // item in hand among them. An item sits in one slot at most. A slot keeps its item when the
    // player runs out of it, so the layout does not move. The server puts an item the player did not
    // have in the first empty slot when they receive it (Place), so it can be held at once, and fills
    // an empty hotbar with what they own (FillFrom: saves from before the hotbar).
    public sealed class Hotbar
    {
        public const int SlotCount = 10;

        private readonly int[] slots = new int[SlotCount];

        public int this[int slot] => slots[slot];

        public int SlotOf(int item)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (slots[slot] == item) return slot;
            }

            return -1;
        }

        // Puts an item in a slot (Items.None empties it). An item moved from another slot swaps with
        // what this slot held. Returns false, changing nothing, for a slot or an item that does not exist.
        public bool Set(int slot, int item)
        {
            if ((uint)slot >= SlotCount || (item != Items.None && !Items.IsValid(item))) return false;

            if (item != Items.None)
            {
                int previous = SlotOf(item);
                if (previous >= 0) slots[previous] = slots[slot];
            }

            slots[slot] = item;
            return true;
        }

        public bool IsEmpty => FirstFilled() < 0;

        // Puts an item the player has just received (and did not have) in the first empty slot, if
        // it has no slot yet and one is empty. Returns true if a slot changed.
        public bool Place(int item)
        {
            if (item == Items.None || SlotOf(item) >= 0) return false;

            int empty = SlotOf(Items.None);
            if (empty < 0) return false;

            slots[empty] = item;
            return true;
        }

        // Gives each owned item without a slot the first empty one, in the inventory's order, while
        // empty slots remain (a player whose save had no hotbar). Returns true if a slot changed.
        public bool FillFrom(Inventory inventory)
        {
            bool changed = false;
            foreach (KeyValuePair<int, int> entry in inventory.Entries)
            {
                if (SlotOf(entry.Key) >= 0) continue;

                int empty = SlotOf(Items.None);
                if (empty < 0) break;

                slots[empty] = entry.Key;
                changed = true;
            }

            return changed;
        }

        private int FirstFilled()
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (slots[slot] != Items.None) return slot;
            }

            return -1;
        }

        public void Clear()
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                slots[slot] = Items.None;
            }
        }

        public void CopyFrom(Hotbar other)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                slots[slot] = other.slots[slot];
            }
        }

        public void Write(BinaryWriter writer)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                writer.Write(slots[slot]);
            }
        }

        // The data may come from a damaged file or another machine: unknown items are refused,
        // retired ones (crafted blocks) leave their slot empty, and an item met twice keeps its first slot.
        public void Read(BinaryReader reader)
        {
            Clear();
            for (int slot = 0; slot < SlotCount; slot++)
            {
                int item = Items.Upgrade(reader.ReadInt32());
                if (item == Items.None || Items.IsRetired(item)) continue;
                if (!Items.IsValid(item)) throw new InvalidDataException($"Invalid hotbar: item {item} in slot {slot}.");

                if (SlotOf(item) < 0) slots[slot] = item;
            }
        }
    }
}
