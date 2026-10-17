using System.Collections.Generic;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class WaterFillTests
    {
        // Flat stone up to y = 7 (FlatGenerator), with a pond of water 2 deep dug at x 4-6, z 4-6.
        private static World PondWorld()
        {
            World world = FlatGenerator.CreateWorld();
            for (int z = 4; z <= 6; z++)
            {
                for (int x = 4; x <= 6; x++)
                {
                    world.SetBlock(x, 7, z, Blocks.Water);
                    world.SetBlock(x, 6, z, Blocks.Water);
                }
            }

            return world;
        }

        [Test]
        public void ABlockBrokenBesideWater_FillsWithIt_DownToTheHoleBelow()
        {
            World world = PondWorld();
            world.SetBlock(7, 7, 5, Blocks.Air);
            world.SetBlock(7, 6, 5, Blocks.Air);
            var fill = new List<BlockPos>();

            Assert.IsTrue(WaterFill.Find(world, new BlockPos(7, 7, 5), fill));
            CollectionAssert.AreEquivalent(new[] { new BlockPos(7, 7, 5), new BlockPos(7, 6, 5) }, fill);
        }

        [Test]
        public void ABlockBrokenUnderWater_Fills()
        {
            World world = PondWorld();
            world.SetBlock(5, 5, 5, Blocks.Air);
            var fill = new List<BlockPos>();

            Assert.IsTrue(WaterFill.Find(world, new BlockPos(5, 5, 5), fill));
            CollectionAssert.AreEqual(new[] { new BlockPos(5, 5, 5) }, fill);
        }

        [Test]
        public void WaterNeverRisesAboveItsLevel()
        {
            World world = PondWorld();

            // The block broken is above the pond's surface: no water touches it from the side or above.
            world.SetBlock(7, 8, 5, Blocks.Stone);
            world.SetBlock(7, 8, 5, Blocks.Air);
            var fill = new List<BlockPos>();

            Assert.IsFalse(WaterFill.Find(world, new BlockPos(7, 8, 5), fill));
            Assert.IsEmpty(fill);
        }

        [Test]
        public void AHoleAwayFromWater_StaysDry()
        {
            World world = PondWorld();
            world.SetBlock(12, 7, 12, Blocks.Air);
            var fill = new List<BlockPos>();

            Assert.IsFalse(WaterFill.Find(world, new BlockPos(12, 7, 12), fill));
        }

        [Test]
        public void ATrenchFromThePond_FillsAlongItsLength_ButNotTheAirAboveIt()
        {
            World world = PondWorld();
            for (int x = 7; x <= 12; x++)
            {
                world.SetBlock(x, 7, 5, Blocks.Air);
            }

            var fill = new List<BlockPos>();
            Assert.IsTrue(WaterFill.Find(world, new BlockPos(7, 7, 5), fill));
            Assert.AreEqual(6, fill.Count);
            foreach (BlockPos pos in fill)
            {
                Assert.AreEqual(7, pos.Y);
            }
        }

        // A breach above open land would pour out over it: too large a space is not filled at all.
        [Test]
        public void OpenSpace_IsNotFilled()
        {
            // 80 × 80 columns: the land outside the basin is larger than WaterFill.MaxBlocks.
            var world = new World(5, 1, 5);
            world.Generate(new FlatGenerator(), 0);

            // A basin wall at y = 8, water inside, then the wall breached towards the open land outside.
            for (int z = 0; z < 6; z++)
            {
                for (int x = 0; x < 6; x++)
                {
                    bool wall = x == 5 || z == 5;
                    world.SetBlock(x, 8, z, wall ? Blocks.Stone : Blocks.Water);
                }
            }

            world.SetBlock(5, 8, 2, Blocks.Air);
            var fill = new List<BlockPos>();

            Assert.IsFalse(WaterFill.Find(world, new BlockPos(5, 8, 2), fill));
            Assert.IsEmpty(fill);
        }
    }
}
