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
        [TestCase(Blocks.Planks)]
        [TestCase(Blocks.Bricks)]
        [TestCase(Blocks.Timbered)]
        public void DropOf_OtherBlocks_IsTheBlockItself(ushort block)
        {
            Assert.AreEqual((int)block, Items.DropOf(block));
        }

        [Test]
        public void DropOf_Air_IsNothing()
        {
            Assert.AreEqual(Items.None, Items.DropOf(Blocks.Air));
        }
    }
}
