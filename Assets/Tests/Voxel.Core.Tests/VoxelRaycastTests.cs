using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class VoxelRaycastTests
    {
        private const float Tolerance = 0.001f;

        [Test]
        public void Cast_Downward_HitsTopFaceOfGround()
        {
            World world = FlatGenerator.CreateWorld();

            bool found = VoxelRaycast.Cast(world, new Vector3(8.5f, 12f, 8.5f), new Vector3(0f, -1f, 0f), 10f, out VoxelHit hit);

            Assert.IsTrue(found);
            Assert.AreEqual(new BlockPos(8, FlatGenerator.GroundHeight - 1, 8), hit.Block);
            Assert.AreEqual(new BlockPos(0, 1, 0), hit.Normal);
            Assert.AreEqual(4f, hit.Distance, Tolerance);
        }

        [Test]
        public void Cast_Horizontal_HitsSideFaceOfBlock()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(12, 10, 8, Blocks.Dirt);

            bool found = VoxelRaycast.Cast(world, new Vector3(8.5f, 10.5f, 8.5f), new Vector3(2f, 0f, 0f), 10f, out VoxelHit hit);

            Assert.IsTrue(found);
            Assert.AreEqual(new BlockPos(12, 10, 8), hit.Block);
            Assert.AreEqual(new BlockPos(-1, 0, 0), hit.Normal);
            Assert.AreEqual(3.5f, hit.Distance, Tolerance);
        }

        [Test]
        public void Cast_BeyondReach_Misses()
        {
            World world = FlatGenerator.CreateWorld();

            bool found = VoxelRaycast.Cast(world, new Vector3(8.5f, 12f, 8.5f), new Vector3(0f, -1f, 0f), 3f, out _);

            Assert.IsFalse(found);
        }

        [Test]
        public void Cast_IntoEmptySky_Misses()
        {
            World world = FlatGenerator.CreateWorld();

            bool found = VoxelRaycast.Cast(world, new Vector3(8.5f, 12f, 8.5f), new Vector3(0f, 1f, 0f), 50f, out _);

            Assert.IsFalse(found);
        }
    }
}
