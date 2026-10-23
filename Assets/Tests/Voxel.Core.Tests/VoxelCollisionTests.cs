using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class VoxelCollisionTests
    {
        private const float HalfWidth = 0.3f;
        private const float Height = 1.8f;
        private const float Tolerance = 0.01f;

        [Test]
        public void Move_FallingInSmallSteps_LandsOnGround()
        {
            World world = FlatGenerator.CreateWorld();
            var result = new MoveResult { Position = new Vector3(8.5f, 12f, 8.5f) };

            for (int i = 0; i < 100; i++)
            {
                result = VoxelCollision.Move(world, result.Position, HalfWidth, Height, new Vector3(0f, -0.1f, 0f));
            }

            Assert.IsTrue(result.Grounded);
            Assert.AreEqual(FlatGenerator.GroundHeight, result.Position.Y, Tolerance);
        }

        [Test]
        public void Move_OneLargeFall_DoesNotTunnelThroughGround()
        {
            World world = FlatGenerator.CreateWorld();

            MoveResult result = VoxelCollision.Move(
                world, new Vector3(8.5f, 12f, 8.5f), HalfWidth, Height, new Vector3(0f, -30f, 0f));

            Assert.IsTrue(result.Grounded);
            Assert.AreEqual(FlatGenerator.GroundHeight, result.Position.Y, Tolerance);
        }

        [Test]
        public void Move_InMidAir_IsNotGrounded()
        {
            World world = FlatGenerator.CreateWorld();

            MoveResult result = VoxelCollision.Move(
                world, new Vector3(8.5f, 12f, 8.5f), HalfWidth, Height, new Vector3(0f, -0.1f, 0f));

            Assert.IsFalse(result.Grounded);
            Assert.AreEqual(11.9f, result.Position.Y, Tolerance);
        }

        [Test]
        public void Move_IntoWall_StopsAtWallAndReportsHit()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(12, FlatGenerator.GroundHeight, 8, Blocks.Stone);
            var start = new Vector3(8.5f, FlatGenerator.GroundHeight + 0.001f, 8.5f);

            MoveResult result = VoxelCollision.Move(world, start, HalfWidth, Height, new Vector3(6f, 0f, 0f));

            Assert.IsTrue(result.HitWall);
            Assert.AreEqual(12f - HalfWidth, result.Position.X, Tolerance);
        }

        // A cell named solid besides the blocks (placed storage) stops a walker and carries one.
        [Test]
        public void Move_ExtraSolidCell_StopsAndCarries()
        {
            World world = FlatGenerator.CreateWorld();
            int ground = FlatGenerator.GroundHeight;
            bool Chest(int x, int y, int z) => x == 12 && y == ground && z == 8;
            var start = new Vector3(8.5f, ground + 0.001f, 8.5f);

            MoveResult walked = VoxelCollision.Move(world, start, HalfWidth, Height, new Vector3(6f, 0f, 0f), Chest);
            MoveResult landed = VoxelCollision.Move(world, new Vector3(12.5f, ground + 1.5f, 8.5f), HalfWidth, Height, new Vector3(0f, -1f, 0f), Chest);
            MoveResult without = VoxelCollision.Move(world, start, HalfWidth, Height, new Vector3(6f, 0f, 0f));

            Assert.IsTrue(walked.HitWall);
            Assert.AreEqual(12f - HalfWidth, walked.Position.X, Tolerance);
            Assert.IsTrue(landed.Grounded);
            Assert.AreEqual(ground + 1f, landed.Position.Y, Tolerance);
            Assert.IsFalse(without.HitWall, "nothing extra without the predicate");
        }

        [Test]
        public void Move_JumpingUnderCeiling_StopsBelowIt()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(8, FlatGenerator.GroundHeight + 3, 8, Blocks.Stone);
            var start = new Vector3(8.5f, FlatGenerator.GroundHeight + 0.001f, 8.5f);

            MoveResult result = VoxelCollision.Move(world, start, HalfWidth, Height, new Vector3(0f, 3f, 0f));

            Assert.IsTrue(result.HitCeiling);
            Assert.AreEqual(FlatGenerator.GroundHeight + 3 - Height, result.Position.Y, Tolerance);
        }

        [Test]
        public void Move_AtWorldEdge_IsHeldInside()
        {
            World world = FlatGenerator.CreateWorld();
            var start = new Vector3(8.5f, FlatGenerator.GroundHeight + 0.001f, 8.5f);

            MoveResult result = VoxelCollision.Move(world, start, HalfWidth, Height, new Vector3(0f, 0f, -20f));

            Assert.IsTrue(result.HitWall);
            Assert.AreEqual(HalfWidth, result.Position.Z, Tolerance);
        }

        [Test]
        public void Overlaps_IsFalseStandingOnGround_AndTrueInsideIt()
        {
            World world = FlatGenerator.CreateWorld();
            var standing = new Vector3(8.5f, FlatGenerator.GroundHeight + 0.001f, 8.5f);
            var buried = new Vector3(8.5f, FlatGenerator.GroundHeight - 0.5f, 8.5f);

            Assert.IsFalse(VoxelCollision.Overlaps(world, standing, HalfWidth, Height));
            Assert.IsTrue(VoxelCollision.Overlaps(world, buried, HalfWidth, Height));
        }

        [Test]
        public void RaiseToFreeSpace_LeavesAFreePositionUntouched()
        {
            World world = FlatGenerator.CreateWorld();
            var standing = new Vector3(8.5f, FlatGenerator.GroundHeight + 0.001f, 8.5f);

            Assert.AreEqual(standing, VoxelCollision.RaiseToFreeSpace(world, standing, HalfWidth, Height));
        }

        [Test]
        public void RaiseToFreeSpace_LiftsAPlayerOutOfBlocksBuiltWhereTheyStood()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(8, FlatGenerator.GroundHeight, 8, Blocks.Stone);
            world.SetBlock(8, FlatGenerator.GroundHeight + 1, 8, Blocks.Stone);
            var saved = new Vector3(8.5f, FlatGenerator.GroundHeight + 0.001f, 8.5f);

            Vector3 raised = VoxelCollision.RaiseToFreeSpace(world, saved, HalfWidth, Height);

            Assert.AreEqual(new Vector3(8.5f, FlatGenerator.GroundHeight + 2f, 8.5f), raised);
            Assert.IsFalse(VoxelCollision.Overlaps(world, raised, HalfWidth, Height));
        }

        [Test]
        public void IntersectsBlock_DetectsOverlapWithPlayerBox()
        {
            var position = new Vector3(8.5f, 8f, 8.5f);

            Assert.IsTrue(VoxelCollision.IntersectsBlock(position, HalfWidth, Height, new BlockPos(8, 9, 8)));
            Assert.IsFalse(VoxelCollision.IntersectsBlock(position, HalfWidth, Height, new BlockPos(8, 10, 8)));
            Assert.IsFalse(VoxelCollision.IntersectsBlock(position, HalfWidth, Height, new BlockPos(9, 8, 8)));
        }
    }
}
