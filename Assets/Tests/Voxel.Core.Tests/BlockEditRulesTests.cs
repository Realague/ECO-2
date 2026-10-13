using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class BlockEditRulesTests
    {
        private const int Ground = FlatGenerator.GroundHeight;

        // Eyes of a player standing at the centre of the flat world.
        private static readonly Vector3 Eyes = new Vector3(8.5f, Ground + PlayerRules.EyeHeight, 8.5f);

        [Test]
        public void BreakingNearbyBlock_IsAllowed()
        {
            World world = FlatGenerator.CreateWorld();

            Assert.IsTrue(BlockEditRules.IsAllowed(world, Eyes, new BlockPos(8, Ground - 1, 10), Blocks.Air));
        }

        [Test]
        public void PlacingInNearbyEmptySpace_IsAllowed()
        {
            World world = FlatGenerator.CreateWorld();

            Assert.IsTrue(BlockEditRules.IsAllowed(world, Eyes, new BlockPos(8, Ground, 10), Blocks.Stone));
        }

        [Test]
        public void BreakingEmptySpace_IsRefused()
        {
            World world = FlatGenerator.CreateWorld();

            Assert.IsFalse(BlockEditRules.IsAllowed(world, Eyes, new BlockPos(8, Ground, 10), Blocks.Air));
        }

        [Test]
        public void PlacingOverExistingBlock_IsRefused()
        {
            World world = FlatGenerator.CreateWorld();

            Assert.IsFalse(BlockEditRules.IsAllowed(world, Eyes, new BlockPos(8, Ground - 1, 10), Blocks.Dirt));
        }

        [Test]
        public void EditBeyondReach_IsRefused()
        {
            World world = FlatGenerator.CreateWorld();
            var eyesInCorner = new Vector3(0.5f, Ground + PlayerRules.EyeHeight, 0.5f);

            Assert.IsFalse(BlockEditRules.IsAllowed(world, eyesInCorner, new BlockPos(15, Ground - 1, 15), Blocks.Air));
        }

        [Test]
        public void EditJustInsideToleratedReach_IsAllowed_AndJustOutside_IsRefused()
        {
            World world = FlatGenerator.CreateWorld();
            float limit = PlayerRules.Reach + BlockEditRules.ReachTolerance;
            var target = new BlockPos(8, Ground - 1, 8);
            var eyesInside = new Vector3(8.5f, Ground + limit - 0.05f, 8.5f);
            var eyesOutside = new Vector3(8.5f, Ground + limit + 0.05f, 8.5f);

            Assert.IsTrue(BlockEditRules.IsAllowed(world, eyesInside, target, Blocks.Air));
            Assert.IsFalse(BlockEditRules.IsAllowed(world, eyesOutside, target, Blocks.Air));
        }

        [Test]
        public void EditOutsideWorld_IsRefused()
        {
            World world = FlatGenerator.CreateWorld();
            var eyesAtEdge = new Vector3(0.5f, Ground + PlayerRules.EyeHeight, 8.5f);

            Assert.IsFalse(BlockEditRules.IsAllowed(world, eyesAtEdge, new BlockPos(-1, Ground, 8), Blocks.Stone));
        }

        [Test]
        public void PlacingUnknownOrOrientedBlock_IsRefused()
        {
            World world = FlatGenerator.CreateWorld();
            var target = new BlockPos(8, Ground, 10);

            Assert.IsFalse(BlockEditRules.IsAllowed(world, Eyes, target, 999));
            Assert.IsFalse(BlockEditRules.IsAllowed(world, Eyes, target, Blocks.WithOrientation(Blocks.Stone, 2)));
        }
    }
}
