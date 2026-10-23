using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class InventoryTests
    {
        [Test]
        public void Add_SameItemTwice_AddsTheQuantities()
        {
            var inventory = new Inventory();

            inventory.Add(Items.WoodOf(Items.OakSpecies), 2);
            inventory.Add(Items.WoodOf(Items.OakSpecies), 3);

            Assert.AreEqual(5, inventory.Count(Items.WoodOf(Items.OakSpecies)));
            Assert.AreEqual(1, inventory.DistinctCount);
        }

        [Test]
        public void Add_NothingOrNonPositiveQuantity_ChangesNothing()
        {
            var inventory = new Inventory();

            inventory.Add(Items.None, 4);
            inventory.Add(Items.WoodOf(Items.OakSpecies), 0);
            inventory.Add(Items.WoodOf(Items.OakSpecies), -2);

            Assert.AreEqual(0, inventory.DistinctCount);
        }

        [Test]
        public void TryRemove_MoreThanOwned_FailsAndKeepsEverything()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 2);

            Assert.IsFalse(inventory.TryRemove(Blocks.Stone, 3));

            Assert.AreEqual(2, inventory.Count(Blocks.Stone));
        }

        [Test]
        public void TryRemove_LastOne_RemovesTheEntry()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 1);

            Assert.IsTrue(inventory.TryRemove(Blocks.Stone));

            Assert.IsFalse(inventory.Has(Blocks.Stone));
            Assert.AreEqual(0, inventory.DistinctCount);
        }

        [Test]
        public void TryRemove_NonPositiveQuantity_Fails()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 5);

            Assert.IsFalse(inventory.TryRemove(Blocks.Stone, 0));
            Assert.IsFalse(inventory.TryRemove(Blocks.Stone, -5));

            Assert.AreEqual(5, inventory.Count(Blocks.Stone));
        }

        [Test]
        public void Entries_AreSortedByItem()
        {
            var inventory = new Inventory();
            inventory.Add(Items.WoodOf(Items.OakSpecies), 1);
            inventory.Add(Blocks.Dirt, 1);
            inventory.Add(Items.Seed(0), 1);
            inventory.Add(Blocks.Stone, 1);

            int[] order = inventory.Entries.Select(entry => entry.Key).ToArray();

            Assert.AreEqual(new[] { (int)Blocks.Stone, Blocks.Dirt, Items.Seed(0), Items.WoodOf(Items.OakSpecies) }, order);
        }

        [Test]
        public void Transfer_MovesTheItemsFromOneInventoryToTheOther()
        {
            var giver = new Inventory();
            var receiver = new Inventory();
            giver.Add(Blocks.Granite, 5);
            receiver.Add(Blocks.Granite, 1);

            Assert.IsTrue(Inventory.Transfer(giver, receiver, Blocks.Granite, 3));

            Assert.AreEqual(2, giver.Count(Blocks.Granite));
            Assert.AreEqual(4, receiver.Count(Blocks.Granite));
        }

        [Test]
        public void Transfer_MoreThanOwned_ChangesNeitherInventory()
        {
            var giver = new Inventory();
            var receiver = new Inventory();
            giver.Add(Blocks.Granite, 2);

            Assert.IsFalse(Inventory.Transfer(giver, receiver, Blocks.Granite, 3));

            Assert.AreEqual(2, giver.Count(Blocks.Granite));
            Assert.AreEqual(0, receiver.DistinctCount);
        }

        [Test]
        public void WriteThenRead_RestoresEveryEntry()
        {
            var original = new Inventory();
            original.Add(Blocks.Stone, 12);
            original.Add(Items.WoodOf(Items.OakSpecies), 7);
            original.Add(Items.Seed(1), 3);

            Inventory loaded = Read(Write(original));

            Assert.AreEqual(3, loaded.DistinctCount);
            Assert.AreEqual(12, loaded.Count(Blocks.Stone));
            Assert.AreEqual(7, loaded.Count(Items.WoodOf(Items.OakSpecies)));
            Assert.AreEqual(3, loaded.Count(Items.Seed(1)));
        }

        [Test]
        public void Read_ReplacesThePreviousContent()
        {
            var original = new Inventory();
            original.Add(Items.WoodOf(Items.OakSpecies), 1);
            var target = new Inventory();
            target.Add(Blocks.Sand, 9);

            using (var reader = new BinaryReader(new MemoryStream(Write(original)), Encoding.UTF8))
            {
                target.Read(reader);
            }

            Assert.AreEqual(1, target.DistinctCount);
            Assert.AreEqual(1, target.Count(Items.WoodOf(Items.OakSpecies)));
        }

        [TestCase(999, 1)]
        [TestCase(Items.None, 1)]
        [TestCase(Items.Straw, 0)]
        [TestCase(Items.Straw, -4)]
        public void Read_UnknownItemOrInvalidQuantity_Throws(int item, int count)
        {
            byte[] data = WriteRaw(1, item, count);

            Assert.Throws<InvalidDataException>(() => Read(data));
        }

        [Test]
        public void Read_SameItemTwice_Throws()
        {
            byte[] data = WriteRaw(2, Items.Straw, 1, Items.Straw, 2);

            Assert.Throws<InvalidDataException>(() => Read(data));
        }

        private static byte[] Write(Inventory inventory)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                inventory.Write(writer);
            }

            return stream.ToArray();
        }

        private static Inventory Read(byte[] data)
        {
            var inventory = new Inventory();
            using (var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8))
            {
                inventory.Read(reader);
            }

            return inventory;
        }

        private static byte[] WriteRaw(params int[] values)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                foreach (int value in values)
                {
                    writer.Write(value);
                }
            }

            return stream.ToArray();
        }
    }
}
