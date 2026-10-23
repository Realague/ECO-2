using System.IO;
using System.Text;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class WoodTests
    {
        private static readonly ItemWeights Weights = new ItemWeights(new float[0]);

        [Test]
        public void EachSpecies_HasItsOwnWood_CountedWithTheBlocks()
        {
            int oak = Items.WoodOf(Items.OakSpecies);
            int birch = Items.WoodOf(2);

            Assert.AreNotEqual(oak, birch);
            Assert.IsTrue(Items.IsValid(birch));
            Assert.IsTrue(Items.IsWood(birch));
            Assert.AreEqual(2, Items.WoodSpecies(birch));
            Assert.IsFalse(Items.IsBlock(birch), "wood is not placed");
            Assert.IsFalse(Items.IsSeed(birch) || Items.IsProduce(birch));
            Assert.IsTrue(CarryLimits.CountsAsBlock(birch));
            Assert.IsTrue(StorageRules.AcceptsInStockpile(birch));
            Assert.IsFalse(StorageRules.AcceptsInChest(birch));
            Assert.IsFalse(Items.IsValid(Items.LegacyWood), "the one wood of before is only read, then upgraded");

            var inventory = new Inventory();
            inventory.Add(oak, 20);
            inventory.Add(birch, 15);
            Assert.AreEqual(35, CarryLimits.BlockCount(inventory));
            Assert.AreEqual(5, CarryLimits.Room(inventory, Blocks.Dirt, Weights));
        }

        [Test]
        public void TheWoodOfBefore_LoadsAsOakWood()
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                // An inventory with 12 of the old wood, then a hotbar with it in slot 3.
                writer.Write(1);
                writer.Write(Items.LegacyWood);
                writer.Write(12);
                for (int slot = 0; slot < Hotbar.SlotCount; slot++) writer.Write(slot == 3 ? Items.LegacyWood : Items.None);

                // A stockpile's piles: 20 old wood in the first.
                writer.Write(Items.LegacyWood);
                writer.Write(20);
                for (int pile = 1; pile < StorageRules.PileCount; pile++)
                {
                    writer.Write(Items.None);
                    writer.Write(0);
                }
            }

            stream.Position = 0;
            var inventory = new Inventory();
            var hotbar = new Hotbar();
            var piles = new StockpilePiles();
            using (var reader = new BinaryReader(stream))
            {
                inventory.Read(reader);
                hotbar.Read(reader);
                piles.Read(reader);
            }

            int oak = Items.WoodOf(Items.OakSpecies);
            Assert.AreEqual(12, inventory.Count(oak));
            Assert.AreEqual(oak, hotbar[3]);
            Assert.AreEqual(oak, piles.ItemAt(0));
            Assert.AreEqual(20, piles.CountAt(0));
        }

        [Test]
        public void AFelledGrownPlant_GivesOneSeedOrTwo_AndAYoungOneNone()
        {
            Assert.AreEqual(2, MiningRules.SeedsFromFelling(true, 0f));
            Assert.AreEqual(2, MiningRules.SeedsFromFelling(true, MiningRules.SecondSeedChance - 0.01f));
            Assert.AreEqual(1, MiningRules.SeedsFromFelling(true, MiningRules.SecondSeedChance));
            Assert.AreEqual(1, MiningRules.SeedsFromFelling(true, 0.99f));
            Assert.AreEqual(0, MiningRules.SeedsFromFelling(false, 0f));
        }
    }
}
