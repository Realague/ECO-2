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
        public void Wood_IsWeighed_NotCountedWithBlocks()
        {
            int oak = Items.WoodOf(Items.OakSpecies);
            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 25);
            inventory.Add(oak, 10);

            Assert.IsFalse(CarryLimits.CountsAsBlock(oak));
            Assert.IsFalse(Items.IsBlock(oak), "it is still not a block to place");
            Assert.AreEqual(25, CarryLimits.BlockCount(inventory));
            Assert.AreEqual(10 * ItemWeights.DefaultWood, CarryLimits.Weight(inventory, Weights), 1e-4f, "a species the table does not know weighs the default");
            Assert.AreEqual(50, CarryLimits.Room(inventory, oak, Weights), "25 kg left at 0.5 kg");
            Assert.AreEqual(15, CarryLimits.Room(inventory, Blocks.Dirt, Weights), "wood leaves the 40 blocks alone");
        }

        [Test]
        public void Wood_WeighsByItsSpecies_AndLessWithAFactor()
        {
            var weights = new ItemWeights(new[] { 0.2f }, null, new[] { 0.8f, 0f });
            Assert.AreEqual(0.8f, weights.Of(Items.WoodOf(0)), 1e-6f);
            Assert.AreEqual(ItemWeights.DefaultWood, weights.Of(Items.WoodOf(1)), 1e-6f, "no weight set: the default");
            Assert.AreEqual(ItemWeights.DefaultWood, weights.Of(Items.WoodOf(5)), 1e-6f);

            ItemWeights lighter = weights.WithWoodFactor(0.75f);
            Assert.AreEqual(0.6f, lighter.Of(Items.WoodOf(0)), 1e-6f);
            Assert.AreEqual(0.2f, lighter.Of(Items.Produce(0)), 1e-6f, "only wood is lighter");
            Assert.AreSame(weights, weights.WithWoodFactor(1f));
        }

        [Test]
        public void Wood_StillGoesOnStockpiles()
        {
            int oak = Items.WoodOf(Items.OakSpecies);
            Assert.IsTrue(Items.GoesOnStockpile(oak));
            Assert.IsTrue(StorageRules.AcceptsInStockpile(oak));
            Assert.IsFalse(StorageRules.AcceptsInChest(oak));
            Assert.IsTrue(Items.GoesOnStockpile(Blocks.Stone));
            Assert.IsFalse(Items.GoesOnStockpile(Blocks.Torch));
            Assert.IsFalse(Items.GoesOnStockpile(Items.Axe));
        }

        // Ores count with the blocks and go on stockpiles; those saved in a chest before stay there.
        [Test]
        public void Ores_CountAsBlocks_AndGoOnStockpiles()
        {
            int iron = Items.Ore(OreKind.Iron);
            Assert.IsTrue(CarryLimits.CountsAsBlock(iron));
            Assert.IsTrue(StorageRules.AcceptsInStockpile(iron));
            Assert.IsFalse(StorageRules.AcceptsInChest(iron));
            Assert.IsTrue(StorageRules.KeptInChest(iron));
            Assert.IsFalse(StorageRules.KeptInChest(Blocks.Stone));

            var inventory = new Inventory();
            inventory.Add(Blocks.Stone, 30);
            inventory.Add(iron, 9);
            Assert.AreEqual(39, CarryLimits.BlockCount(inventory));
            Assert.AreEqual(1, CarryLimits.Room(inventory, Items.Ore(OreKind.Coal), Weights));
            Assert.AreEqual(1, CarryLimits.Room(inventory, Blocks.Dirt, Weights));
            Assert.AreEqual(0f, CarryLimits.Weight(inventory, Weights), "ores weigh nothing in the bag");
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
