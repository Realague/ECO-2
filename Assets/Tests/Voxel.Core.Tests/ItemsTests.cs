using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ItemsTests
    {
        [Test]
        public void EveryBlockType_IsAnItemThatGivesTheSameBlockBack()
        {
            for (ushort type = Blocks.Stone; type <= Blocks.LastType; type++)
            {
                if (Blocks.IsRetired(type)) continue;

                int item = Items.OfBlock(type);

                Assert.IsTrue(Items.IsBlock(item));
                Assert.IsTrue(Items.IsValid(item));
                Assert.AreEqual(type, Items.BlockOf(item));
            }
        }

        [Test]
        public void Air_IsNotAnItem()
        {
            Assert.AreEqual(Items.None, Items.OfBlock(Blocks.Air));
            Assert.IsFalse(Items.IsBlock(Items.None));
            Assert.IsFalse(Items.IsValid(Items.None));
        }

        [Test]
        public void Seed_KeepsItsSpecies()
        {
            int seed = Items.Seed(7);

            Assert.IsTrue(Items.IsSeed(seed));
            Assert.IsTrue(Items.IsValid(seed));
            Assert.IsFalse(Items.IsBlock(seed));
            Assert.AreEqual(7, Items.SeedSpecies(seed));
        }

        [Test]
        public void Wood_IsNeitherABlockNorASeed()
        {
            Assert.IsTrue(Items.IsValid(Items.WoodOf(Items.OakSpecies)));
            Assert.IsFalse(Items.IsBlock(Items.WoodOf(Items.OakSpecies)));
            Assert.IsFalse(Items.IsSeed(Items.WoodOf(Items.OakSpecies)));
        }

        [Test]
        public void Produce_KeepsItsSpecies_AndIsNoSeed()
        {
            int crop = Items.Produce(7);

            Assert.IsTrue(Items.IsProduce(crop));
            Assert.IsTrue(Items.IsValid(crop));
            Assert.IsFalse(Items.IsSeed(crop));
            Assert.IsFalse(Items.IsBlock(crop));
            Assert.AreEqual(7, Items.ProduceSpecies(crop));
            Assert.IsFalse(Items.IsProduce(Items.Seed(7)));
            Assert.IsFalse(Items.IsProduce(Items.Seed(Items.MaxSeedSpecies - 1)));
        }

        [Test]
        public void Carcass_KeepsItsSpecies_AndIsNeitherABlockNorFood()
        {
            int carcass = Items.Carcass(3);

            Assert.IsTrue(Items.IsCarcass(carcass));
            Assert.IsTrue(Items.IsValid(carcass));
            Assert.AreEqual(3, Items.CarcassSpecies(carcass));
            Assert.IsFalse(Items.IsBlock(carcass));
            Assert.IsFalse(Items.IsProduce(carcass));
            Assert.IsFalse(Items.IsWood(carcass));
            Assert.IsFalse(CarryLimits.CountsAsBlock(carcass), "it weighs, like every item that is not a block");
            Assert.IsTrue(StorageRules.AcceptsInChest(carcass));
            Assert.IsFalse(Items.IsCarcass(Items.Carcass(Items.MaxCarcassSpecies - 1) + 1));
        }

        [Test]
        public void CarcassWeight_GoesWithTheAnimalsSize_AndAlwaysFitsInABag()
        {
            var weights = new ItemWeights(new float[0], new[] { ItemWeights.CarcassWeightFor(30f), ItemWeights.CarcassWeightFor(60f), ItemWeights.CarcassWeightFor(100f) });

            Assert.Less(weights.Of(Items.Carcass(0)), weights.Of(Items.Carcass(1)));
            Assert.Less(weights.Of(Items.Carcass(1)), weights.Of(Items.Carcass(2)));
            Assert.AreEqual(ItemWeights.HeaviestCarcass, weights.Of(Items.Carcass(2)));

            // Even the heaviest fits beside what every player is given at first.
            var bag = new Inventory();
            foreach (int tool in Items.Tools) bag.Add(tool, 1);
            bag.Add(Items.Arrow, 20);
            bag.Add(Items.Chest, 1);
            bag.Add(Items.Stockpile, 1);
            Assert.Greater(CarryLimits.Room(bag, Items.Carcass(2), weights), 0, "a bear's carcass fits beside the starting kit");
            Assert.Greater(weights.Of(Items.Carcass(7)), 0f, "a species the table does not know still weighs");
        }

        [Test]
        public void Straw_IsAnItemOfItsOwn()
        {
            Assert.IsTrue(Items.IsValid(Items.Straw));
            Assert.IsFalse(Items.IsBlock(Items.Straw));
            Assert.IsFalse(Items.IsSeed(Items.Straw));
            Assert.IsFalse(Items.IsProduce(Items.Straw));
            Assert.AreEqual("Straw", Items.NameOf(Items.Straw));
        }

        [Test]
        public void DropOf_Grass_IsDirt()
        {
            Assert.AreEqual((int)Blocks.Dirt, Items.DropOf(Blocks.Grass));
        }

        [TestCase(Blocks.Stone)]
        [TestCase(Blocks.Dirt)]
        [TestCase(Blocks.Sand)]
        [TestCase(Blocks.IronOre)]
        public void DropOf_OtherBlocks_IsTheBlockItself(ushort block)
        {
            Assert.AreEqual((int)block, Items.DropOf(block));
        }

        // Planks, bricks, timber frame (4-6) and the cut rocks (26-31) were removed with crafting.
        [TestCase(4)]
        [TestCase(6)]
        [TestCase(26)]
        [TestCase(31)]
        public void RemovedCraftedBlocks_AreNeitherBlocksNorItems(int type)
        {
            Assert.IsTrue(Blocks.IsRetired((ushort)type));
            Assert.IsFalse(Blocks.IsValid((ushort)type));
            Assert.IsFalse(Items.IsBlock(type));
            Assert.IsFalse(Items.IsValid(type));
            Assert.IsTrue(Items.IsRetired(type));
        }

        [Test]
        public void ExistingBlocks_AreNotRetired()
        {
            for (ushort type = Blocks.Air; type <= Blocks.LastType; type++)
            {
                if ((type >= 4 && type <= 6) || (type >= 26 && type <= 31)) continue;
                Assert.IsFalse(Blocks.IsRetired(type), Items.NameOf(type));
            }
        }

        [Test]
        public void DropOf_Air_IsNothing()
        {
            Assert.AreEqual(Items.None, Items.DropOf(Blocks.Air));
        }
    }
}
