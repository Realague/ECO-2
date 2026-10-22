using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class CarryLimitsTests
    {
        // Crop 0 weighs 0.2 kg, crop 1 one kg.
        private static readonly ItemWeights Weights = new ItemWeights(new[] { 0.2f, 1f });

        [Test]
        public void Blocks_AreCountedTogether_AndWeighNothing()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 25);
            inventory.Add(Blocks.Dirt, 10);

            Assert.AreEqual(35, CarryLimits.BlockCount(inventory));
            Assert.AreEqual(0f, CarryLimits.Weight(inventory, Weights));
            Assert.AreEqual(5, CarryLimits.Room(inventory, Blocks.Sand, Weights), "40 blocks of any type");
        }

        [Test]
        public void Weight_IsShared_ByEverythingThatIsNotABlock()
        {
            var inventory = new Inventory();
            inventory.Add(Items.Produce(1), 20);
            inventory.Add(Items.Produce(0), 10);
            inventory.Add(Items.Seed(3), 100);

            Assert.AreEqual(20f + 2f + 1f, CarryLimits.Weight(inventory, Weights), 1e-4f);
            Assert.AreEqual(7, CarryLimits.Room(inventory, Items.Produce(1), Weights));
            Assert.AreEqual(35, CarryLimits.Room(inventory, Items.Produce(0), Weights), "7 kg of 0.2 kg crops");
            Assert.AreEqual(CarryLimits.MaxBlocks, CarryLimits.Room(inventory, Blocks.Stone, Weights), "blocks do not take weight");
        }

        [Test]
        public void Wood_CountsAsBlocks_AndWeighsNothing()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 25);
            inventory.Add(Items.Wood, 10);

            Assert.IsTrue(CarryLimits.CountsAsBlock(Items.Wood));
            Assert.IsFalse(Items.IsBlock(Items.Wood), "it is still not a block to place");
            Assert.AreEqual(35, CarryLimits.BlockCount(inventory));
            Assert.AreEqual(0f, CarryLimits.Weight(inventory, Weights));
            Assert.AreEqual(5, CarryLimits.Room(inventory, Items.Wood, Weights));
            Assert.AreEqual(5, CarryLimits.Room(inventory, Blocks.Dirt, Weights), "wood and blocks share the 40");
        }

        [Test]
        public void Room_ForWhatExactlyFits_IsNotRefused()
        {
            var inventory = new Inventory();
            inventory.Add(Items.Produce(0), 149);

            Assert.AreEqual(1, CarryLimits.Room(inventory, Items.Produce(0), Weights), "149 x 0.2 + 0.2 is 30 kg");
        }

        [Test]
        public void AnInventoryBeyondALimit_IsKept_ButTakesNoMore()
        {
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 500);
            inventory.Add(Items.Produce(1), 31);

            Assert.AreEqual(0, CarryLimits.Room(inventory, Blocks.Dirt, Weights));
            Assert.AreEqual(0, CarryLimits.Room(inventory, Items.Straw, Weights));
            Assert.AreEqual(500, inventory.Count(Blocks.Stone));
        }
    }
}
