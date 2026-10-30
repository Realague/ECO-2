using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class DoorTests
    {
        private const int Ground = FlatGenerator.GroundHeight;

        [Test]
        public void ADoor_HoldsItsFacingOpenAndHalf_InItsOrientation()
        {
            for (int facing = 0; facing < 4; facing++)
            {
                foreach (bool open in new[] { false, true })
                {
                    foreach (bool upper in new[] { false, true })
                    {
                        ushort door = Doors.Make(facing, open, upper);
                        Assert.IsTrue(Blocks.IsDoor(door));
                        Assert.IsTrue(Blocks.IsValid(door));
                        Assert.AreEqual(facing, Doors.FacingOf(door));
                        Assert.AreEqual(open, Doors.IsOpen(door));
                        Assert.AreEqual(upper, Doors.IsUpper(door));
                        Assert.AreEqual(!open, Doors.IsOpen(Doors.Toggled(door)));
                        Assert.AreEqual(upper, Doors.IsUpper(Doors.Toggled(door)));
                    }
                }
            }
        }

        [Test]
        public void AClosedDoor_IsSolid_AnOpenOneNot_AndNeitherIsOpaque()
        {
            ushort closed = Doors.Make(0, false, false);
            ushort open = Doors.Make(0, true, false);

            Assert.IsTrue(Blocks.IsSolid(closed));
            Assert.IsFalse(Blocks.IsSolid(open));
            Assert.IsFalse(Blocks.IsOpaque(closed), "faces behind it are drawn and light goes through");
            Assert.IsTrue(Blocks.FillsColumn(closed), "animals cannot climb it");
            Assert.IsFalse(Blocks.FillsColumn(open));
            Assert.IsTrue(Blocks.IsBreakable(open));
            Assert.IsFalse(Blocks.IsPlaceable(closed), "placed by its own rules, never as a block");
            Assert.AreEqual(Items.Hammer, ToolRules.ToBreak(open));
            Assert.AreEqual(1, MiningRules.HitsToBreak(closed));
            Assert.AreEqual((int)Blocks.Door, Items.DropOf(Doors.Make(2, true, true)));
            Assert.IsFalse(CarryLimits.CountsAsBlock(Blocks.Door), "it weighs, like a torch");
            Assert.IsTrue(StorageRules.AcceptsInChest(Blocks.Door));
        }

        [Test]
        public void ThePanel_StandsAcrossTheFarEdge_AndSwingsAgainstTheHingeSide()
        {
            var cell = new BlockPos(4, 9, 6);

            Doors.GetBox(cell, Doors.Make(0, false, false), out Vector3 min, out Vector3 max);
            Assert.AreEqual(new Vector3(4f, 9f, 7f - Doors.Thickness), min);
            Assert.AreEqual(new Vector3(5f, 10f, 7f), max);

            Doors.GetBox(cell, Doors.Make(0, true, false), out min, out max);
            Assert.AreEqual(new Vector3(4f, 9f, 6f), min);
            Assert.AreEqual(new Vector3(4f + Doors.Thickness, 10f, 7f), max);

            // Facing +x: across the +x edge closed.
            Doors.GetBox(cell, Doors.Make(1, false, false), out min, out max);
            Assert.AreEqual(5f - Doors.Thickness, min.X, 1e-5f);
            Assert.AreEqual(5f, max.X, 1e-5f);
            Assert.AreEqual(6f, min.Z, 1e-5f);
            Assert.AreEqual(7f, max.Z, 1e-5f);
        }

        [Test]
        public void ADoor_StandsOnSolidGround_InTwoEmptyCells_AndIsHeldByIt()
        {
            World world = FlatGenerator.CreateWorld();
            var lower = new BlockPos(5, Ground, 5);

            Assert.IsTrue(Doors.CanStand(world, lower));
            Assert.IsFalse(Doors.CanStand(world, new BlockPos(5, Ground + 2, 5)), "nothing under it");

            world.SetBlock(5, Ground + 1, 5, Blocks.Stone);
            Assert.IsFalse(Doors.CanStand(world, lower), "no room for its upper half");
            world.SetBlock(5, Ground + 1, 5, Blocks.Air);

            world.SetBlock(5, Ground, 5, Doors.Make(0, false, false));
            world.SetBlock(5, Ground + 1, 5, Doors.Make(0, false, true));
            Assert.IsTrue(Doors.TryFindHeldBy(world, new BlockPos(5, Ground - 1, 5), out BlockPos held));
            Assert.AreEqual(lower, held);
            Assert.AreEqual(new BlockPos(5, Ground + 1, 5), Doors.OtherHalf(lower, world.GetBlock(lower)));
            Assert.AreEqual(lower, Doors.LowerHalf(new BlockPos(5, Ground + 1, 5), world.GetBlock(5, Ground + 1, 5)));
            Assert.IsFalse(Doors.CanStand(world, new BlockPos(5, Ground + 2, 5)), "not on another door");
            Assert.IsFalse(Torches.HasSupport(world, new BlockPos(5, Ground + 2, 5), Blocks.WithOrientation(Blocks.Torch, Torches.Standing)));
            Assert.IsFalse(StorageRules.CanPlaceChest(world, new BlockPos(5, Ground + 2, 5)));
        }

        [Test]
        public void ARay_StopsAtThePanel_OpenOrClosed_WhenItLooksForThinBlocks()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(5, Ground, 5, Doors.Make(0, true, false));
            world.SetBlock(5, Ground + 1, 5, Doors.Make(0, true, true));
            var origin = new Vector3(5.05f, Ground + 0.5f, 3f);

            Assert.IsTrue(VoxelRaycast.Cast(world, origin, Vector3.UnitZ, 6f, true, out VoxelHit hit));
            Assert.AreEqual(new BlockPos(5, Ground, 5), hit.Block, "the open panel along the hinge side");
            Assert.IsFalse(VoxelRaycast.Cast(world, new Vector3(5.5f, Ground + 0.5f, 3f), Vector3.UnitZ, 6f, true, out _), "past the open panel");
            Assert.IsFalse(VoxelRaycast.Cast(world, origin, Vector3.UnitZ, 6f, false, out _), "an open door is not solid");

            world.SetBlock(5, Ground, 5, Doors.Make(0, false, false));
            Assert.IsTrue(VoxelRaycast.Cast(world, new Vector3(5.5f, Ground + 0.5f, 3f), Vector3.UnitZ, 6f, false, out hit));
            Assert.AreEqual(5f - 3f, hit.Distance, 1e-4f, "closed, it stops a ray at its cell");
        }
    }
}
