using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class HotbarTests
    {
        [Test]
        public void FillFrom_GivesOwnedItemsWithoutASlotTheFirstEmptySlots()
        {
            var hotbar = new Hotbar();
            hotbar.Set(0, Items.Wood);
            var inventory = new Inventory();
            inventory.Add(Items.Wood, 3);
            inventory.Add(Blocks.Stone, 5);
            inventory.Add(Blocks.Dirt, 2);

            Assert.IsTrue(hotbar.FillFrom(inventory));

            Assert.AreEqual(Items.Wood, hotbar[0], "an item keeps its slot");
            Assert.AreEqual(Blocks.Stone, hotbar[1], "then the inventory's order");
            Assert.AreEqual(Blocks.Dirt, hotbar[2]);
            Assert.IsFalse(hotbar.FillFrom(inventory), "nothing new to place");
        }

        [Test]
        public void FillFrom_StopsWhenEverySlotIsTaken()
        {
            var hotbar = new Hotbar();
            var inventory = new Inventory();
            for (int i = 0; i < Hotbar.SlotCount + 2; i++)
            {
                inventory.Add(Items.Seed(i), 1);
            }

            hotbar.FillFrom(inventory);

            for (int slot = 0; slot < Hotbar.SlotCount; slot++)
            {
                Assert.AreEqual(Items.Seed(slot), hotbar[slot]);
            }

            Assert.AreEqual(-1, hotbar.SlotOf(Items.Seed(Hotbar.SlotCount)));
        }

        [Test]
        public void ASlotKeepsItsItem_WhenThePlayerRunsOutOfIt()
        {
            var hotbar = new Hotbar();
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 1);
            hotbar.FillFrom(inventory);
            inventory.TryRemove(Blocks.Stone);
            inventory.Add(Blocks.Dirt, 1);

            hotbar.FillFrom(inventory);

            Assert.AreEqual(Blocks.Stone, hotbar[0], "the layout does not move");
            Assert.AreEqual(Blocks.Dirt, hotbar[1]);
        }

        [Test]
        public void Place_PutsANewItemInTheFirstEmptySlot_UnlessItHasOne()
        {
            var hotbar = new Hotbar();
            Assert.IsTrue(hotbar.IsEmpty);
            hotbar.Set(0, Items.Wood);

            Assert.IsTrue(hotbar.Place(Blocks.Stone));
            Assert.AreEqual(Blocks.Stone, hotbar[1]);
            Assert.IsFalse(hotbar.Place(Items.Wood), "already in a slot");
            Assert.IsFalse(hotbar.IsEmpty);
        }

        [Test]
        public void Set_MovesAnItemOutOfItsSlot_AndRefusesWhatDoesNotExist()
        {
            var hotbar = new Hotbar();
            Assert.IsTrue(hotbar.Set(2, Items.Wood));

            Assert.IsTrue(hotbar.Set(7, Items.Wood));
            Assert.AreEqual(Items.None, hotbar[2], "an item sits in one slot at most");
            Assert.AreEqual(Items.Wood, hotbar[7]);

            hotbar.Set(3, Blocks.Stone);
            Assert.IsTrue(hotbar.Set(3, Items.Wood));
            Assert.AreEqual(Blocks.Stone, hotbar[7], "moved onto a taken slot, the items swap");
            Assert.AreEqual(Items.Wood, hotbar[3]);
            hotbar.Set(7, Items.Wood);

            Assert.IsTrue(hotbar.Set(7, Items.None), "a slot can be emptied");
            Assert.AreEqual(Items.None, hotbar[7]);

            Assert.IsFalse(hotbar.Set(Hotbar.SlotCount, Items.Wood));
            Assert.IsFalse(hotbar.Set(0, 99999));
            Assert.IsFalse(hotbar.Set(0, 4), "a retired block");
        }

        [Test]
        public void Read_DropsRetiredItems_AndRefusesUnknownOnes()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(Items.Wood);
                writer.Write(4);
                for (int slot = 2; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
            }

            stream.Position = 0;
            var hotbar = new Hotbar();
            hotbar.Read(new BinaryReader(stream));
            Assert.AreEqual(Items.Wood, hotbar[0]);
            Assert.AreEqual(Items.None, hotbar[1], "a retired crafted block leaves its slot empty");

            var bad = new MemoryStream();
            using (var writer = new BinaryWriter(bad, System.Text.Encoding.UTF8, true))
            {
                writer.Write(99999);
                for (int slot = 1; slot < Hotbar.SlotCount; slot++) writer.Write(Items.None);
            }

            bad.Position = 0;
            Assert.Throws<InvalidDataException>(() => new Hotbar().Read(new BinaryReader(bad)));
        }
    }
}
