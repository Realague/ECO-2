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
            Assert.IsTrue(Items.IsValid(Items.Wood));
            Assert.IsFalse(Items.IsBlock(Items.Wood));
            Assert.IsFalse(Items.IsSeed(Items.Wood));
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
