using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ShapesTests
    {
        private const int Ground = FlatGenerator.GroundHeight;
        private const float HalfWidth = 0.3f;
        private const float Height = 1.8f;
        private const float Tolerance = 0.01f;

        private static ushort Planks()
        {
            Assert.IsTrue(WoodBlocks.TryPlanksOf(0, out ushort planks));
            return planks;
        }

        [Test]
        public void EveryShapeAndFacing_ReadsBack_AndGivesThePlainBlockBack()
        {
            ushort planks = Planks();
            foreach (Shape shape in Shapes.All)
            {
                for (int facing = 0; facing < 4; facing++)
                {
                    ushort block = Shapes.Make(planks, shape, facing);
                    Assert.AreEqual(shape, Shapes.ShapeOf(block));
                    Assert.IsTrue(Blocks.IsValid(block), $"{shape} {facing}");
                    Assert.IsTrue(Blocks.IsSolid(block) && Blocks.IsPlaceable(block) && Blocks.IsConstruction(block));
                    Assert.AreEqual((int)planks, Items.DropOf(block), "a shape takes and gives back one block");
                    if (Shapes.HasFacing(shape)) Assert.AreEqual(facing, Shapes.FacingOf(block));
                }
            }

            Assert.IsFalse(Blocks.IsValid(Blocks.WithOrientation(planks, Shapes.LastOrientation + 1)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithOrientation(Blocks.Stone, 1)), "natural blocks have no shapes");
            Assert.AreEqual(Shape.Cube, Shapes.ShapeOf(Blocks.WithOrientation(Blocks.Torch, 1)), "a torch's orientation is not a shape");

            // The variant bits: only wood blocks use them, and only variant 1; the type reads past them.
            ushort stairs = Shapes.Make(planks, Shape.Stairs, 2);
            Assert.AreEqual(planks, Blocks.TypeOf(stairs));
            Assert.AreEqual(1, Blocks.VariantOf(stairs));
            Assert.IsTrue(WoodBlocks.IsPlanks(stairs));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(planks, 2, 0)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Stone, 1, 0)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Torch, 1, Torches.Standing)));
        }

        [Test]
        public void Stairs_AreTwoHalfSteps_AndClimbedWithoutJumping()
        {
            World world = FlatGenerator.CreateWorld();
            var boxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetBoxes(world, new BlockPos(0, 0, 0), Shapes.Make(Planks(), Shape.Stairs, 1), boxes);
            Assert.AreEqual(2, boxes.Count);
            Assert.AreEqual(new Vector3(0.5f, 0.5f, 0f), boxes[1].min, "the high step toward +x");

            // Stairs up to a block on a full block: walked up from the ground.
            world.SetBlock(10, Ground, 8, Shapes.Make(Planks(), Shape.Stairs, 1));
            world.SetBlock(11, Ground, 8, Planks());
            world.SetBlock(12, Ground, 8, Planks());
            var position = new Vector3(8.5f, Ground, 8.5f);
            for (int i = 0; i < 40; i++)
            {
                position = VoxelCollision.Move(world, position, HalfWidth, Height, new Vector3(0.1f, -0.05f, 0f)).Position;
            }

            Assert.AreEqual(12.5f, position.X, Tolerance);
            Assert.AreEqual(Ground + 1f, position.Y, Tolerance);
        }

        // Corners turn toward the diagonal looked along; the outer one rises toward that corner, the
        // inner one toward the two edges about it.
        [Test]
        public void RoofCorners_RiseTowardTheCornerLookedAt()
        {
            Assert.AreEqual(0, Shapes.CornerFacingOf(45f), "toward +x +z");
            Assert.AreEqual(1, Shapes.CornerFacingOf(135f), "toward +x -z");
            Assert.AreEqual(2, Shapes.CornerFacingOf(-135f));
            Assert.AreEqual(3, Shapes.CornerFacingOf(-45f));

            World world = FlatGenerator.CreateWorld();
            var boxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetBoxes(world, new BlockPos(0, 0, 0), Shapes.Make(Planks(), Shape.OuterCorner, 1), boxes);
            Assert.AreEqual(4, boxes.Count);
            Assert.AreEqual(new Vector3(0.75f, 0.75f, 0f), boxes[3].min, "the top step in the +x -z corner");
            Assert.AreEqual(new Vector3(1f, 1f, 0.25f), boxes[3].max);

            boxes.Clear();
            Shapes.GetBoxes(world, new BlockPos(0, 0, 0), Shapes.Make(Planks(), Shape.InnerCorner, 0), boxes);
            Assert.AreEqual(7, boxes.Count, "a full first step, then two strips a step");
            Assert.IsTrue(Covers(boxes, new Vector3(0.9f, 0.95f, 0.1f)), "high along x 1");
            Assert.IsTrue(Covers(boxes, new Vector3(0.1f, 0.95f, 0.9f)), "high along z 1");
            Assert.IsFalse(Covers(boxes, new Vector3(0.1f, 0.6f, 0.1f)), "low at the corner 0, 0");
            Assert.IsTrue(Blocks.BlocksLight(Shapes.Make(Planks(), Shape.InnerCorner, 0)));
        }

        private static bool Covers(List<(Vector3 min, Vector3 max)> boxes, Vector3 point)
        {
            foreach ((Vector3 min, Vector3 max) in boxes)
            {
                if (point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y && point.Z >= min.Z && point.Z <= max.Z) return true;
            }

            return false;
        }

        // A ladder stands against the edge of its facing, lets the light by, and is touched by a player
        // standing before it, not by one a block away.
        [Test]
        public void ALadder_IsTouchedByAPlayerStandingBeforeIt()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(8, Ground, 9, Planks());
            world.SetBlock(8, Ground + 1, 9, Planks());
            ushort ladder = Shapes.Make(Planks(), Shape.Ladder, 0);
            world.SetBlock(8, Ground, 8, ladder);
            world.SetBlock(8, Ground + 1, 8, ladder);

            Assert.IsFalse(Blocks.BlocksLight(ladder) || Blocks.IsOpaque(ladder));
            Assert.IsTrue(Shapes.StopsAnimals(ladder));
            Assert.IsTrue(Shapes.TouchesLadder(world, new Vector3(8.5f, Ground, 8.5f), HalfWidth, Height));
            Assert.IsTrue(Shapes.TouchesLadder(world, new Vector3(8.5f, Ground + 1.5f, 8.5f), HalfWidth, Height), "halfway up");
            Assert.IsFalse(Shapes.TouchesLadder(world, new Vector3(8.5f, Ground, 7.2f), HalfWidth, Height));
            Assert.IsFalse(Shapes.TouchesLadder(world, new Vector3(8.5f, Ground + 2f, 8.5f), HalfWidth, Height), "above its top");
        }

        [Test]
        public void OnlyAFullBlockIsOpaque_ButSlabsWallsAndSlopesHoldTheLight()
        {
            ushort planks = Planks();
            Assert.IsTrue(Blocks.IsOpaque(planks) && Blocks.BlocksLight(planks));
            foreach (Shape shape in new[] { Shape.Wall, Shape.Floor, Shape.FlatRoof, Shape.Slope })
            {
                ushort block = Shapes.Make(planks, shape, 0);
                Assert.IsFalse(Blocks.IsOpaque(block), shape.ToString());
                Assert.IsTrue(Blocks.BlocksLight(block), shape.ToString());
            }

            foreach (Shape shape in new[] { Shape.Post, Shape.Fence })
            {
                ushort block = Shapes.Make(planks, shape, 0);
                Assert.IsFalse(Blocks.IsOpaque(block) || Blocks.BlocksLight(block), shape.ToString());
                Assert.IsTrue(Shapes.StopsAnimals(block));
            }

            Assert.IsTrue(Shapes.StopsAnimals(Shapes.Make(planks, Shape.Wall, 2)));
            Assert.IsFalse(Shapes.StopsAnimals(Shapes.Make(planks, Shape.Floor, 0)));
            Assert.IsFalse(Shapes.StopsAnimals(planks));
        }

        // A wall stands against the edge of its cell the player faced: +z for facing 0, +x for 1, -z, -x.
        [TestCase(0, 0f, 0.75f, 1f, 1f)]
        [TestCase(1, 0.75f, 0f, 1f, 1f)]
        [TestCase(2, 0f, 0f, 1f, 0.25f)]
        [TestCase(3, 0f, 0f, 0.25f, 1f)]
        public void AWall_StandsAgainstTheEdgeItFaces(int facing, float minX, float minZ, float maxX, float maxZ)
        {
            World world = FlatGenerator.CreateWorld();
            var boxes = new List<(Vector3 min, Vector3 max)>();
            var cell = new BlockPos(4, Ground, 6);

            Shapes.GetBoxes(world, cell, Shapes.Make(Planks(), Shape.Wall, facing), boxes);

            Assert.AreEqual(1, boxes.Count);
            Assert.AreEqual(new Vector3(4f + minX, Ground, 6f + minZ), boxes[0].min);
            Assert.AreEqual(new Vector3(4f + maxX, Ground + 1f, 6f + maxZ), boxes[0].max);
        }

        [Test]
        public void ASlope_IsFourSteps_RisingTowardItsFacing()
        {
            World world = FlatGenerator.CreateWorld();
            var boxes = new List<(Vector3 min, Vector3 max)>();

            Shapes.GetBoxes(world, new BlockPos(0, 0, 0), Shapes.Make(Planks(), Shape.Slope, 1), boxes);

            Assert.AreEqual(4, boxes.Count);
            for (int step = 0; step < 4; step++)
            {
                Assert.AreEqual(step * 0.25f, boxes[step].min.X, 1e-5f, "it rises toward +x");
                Assert.AreEqual((step + 1) * 0.25f, boxes[step].max.Y, 1e-5f);
                Assert.AreEqual(1f, boxes[step].max.X, 1e-5f);
            }
        }

        // A fence joins the fences beside it with rails, and stands taller for collision than for aiming.
        [Test]
        public void AFence_JoinsTheFencesBesideIt()
        {
            World world = FlatGenerator.CreateWorld();
            ushort fence = Shapes.Make(Planks(), Shape.Fence, 0);
            world.SetBlock(5, Ground, 5, fence);
            world.SetBlock(6, Ground, 5, fence);
            world.SetBlock(5, Ground, 6, fence);
            world.SetBlock(4, Ground, 5, Planks());
            var boxes = new List<(Vector3 min, Vector3 max)>();

            Shapes.GetBoxes(world, new BlockPos(5, Ground, 5), fence, boxes);
            Assert.AreEqual(3, boxes.Count, "a post and two rails: a full block is not a fence");
            foreach ((Vector3 _, Vector3 max) in boxes) Assert.AreEqual(Ground + 1f, max.Y);

            boxes.Clear();
            Shapes.GetBoxes(world, new BlockPos(5, Ground, 5), fence, boxes, true);
            foreach ((Vector3 _, Vector3 max) in boxes) Assert.AreEqual(Ground + Shapes.FenceHeight, max.Y);
        }

        [Test]
        public void ATorchADoorAndAChest_NeedAFullBlockUnderThem()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(5, Ground, 5, Shapes.Make(Planks(), Shape.Floor, 0));
            world.SetBlock(7, Ground, 5, Planks());

            Assert.IsFalse(Torches.HasSupport(world, new BlockPos(5, Ground + 1, 5), Torches.Standing));
            Assert.IsFalse(Doors.CanStand(world, new BlockPos(5, Ground + 1, 5)));
            Assert.IsFalse(StorageRules.CanPlaceChest(world, new BlockPos(5, Ground + 1, 5)));
            Assert.IsTrue(Torches.HasSupport(world, new BlockPos(7, Ground + 1, 5), Torches.Standing));
            Assert.IsTrue(StorageRules.CanPlaceChest(world, new BlockPos(7, Ground + 1, 5)));
        }

        [Test]
        public void ALine_FollowsTheAxisOfTheLargestGap_SixteenAtMost()
        {
            var cells = new List<BlockPos>();

            BuildLines.Cells(new BlockPos(2, 5, 2), new BlockPos(6, 6, 3), cells);
            Assert.AreEqual(5, cells.Count);
            Assert.AreEqual(new BlockPos(2, 5, 2), cells[0]);
            Assert.AreEqual(new BlockPos(6, 5, 2), cells[4], "along x, at the start's height and z");

            BuildLines.Cells(new BlockPos(2, 5, 2), new BlockPos(2, 5, -40), cells);
            Assert.AreEqual(BuildLines.MaxLength, cells.Count);
            Assert.AreEqual(new BlockPos(2, 5, 2 - BuildLines.MaxLength + 1), cells[BuildLines.MaxLength - 1]);

            BuildLines.Cells(new BlockPos(1, 1, 1), new BlockPos(1, 4, 1), cells);
            Assert.AreEqual(new BlockPos(1, 4, 1), cells[3], "up, when that is the largest gap");

            BuildLines.Cells(new BlockPos(3, 3, 3), new BlockPos(3, 3, 3), cells);
            Assert.AreEqual(1, cells.Count, "one block");
        }

        // A wall stops a walker at its face, not at its cell's edge.
        [Test]
        public void AWall_StopsAWalkerAtItsFace()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(12, Ground, 8, Shapes.Make(Planks(), Shape.Wall, 1));
            world.SetBlock(12, Ground + 1, 8, Shapes.Make(Planks(), Shape.Wall, 1));

            MoveResult result = VoxelCollision.Move(world, new Vector3(8.5f, Ground, 8.5f), HalfWidth, Height, new Vector3(6f, 0f, 0f));

            Assert.IsTrue(result.HitWall);
            Assert.AreEqual(12.75f - HalfWidth, result.Position.X, Tolerance);
        }

        // Half a block is climbed without jumping, a full block is not.
        [Test]
        public void AFloor_IsClimbedWithoutJumping()
        {
            World world = FlatGenerator.CreateWorld();
            for (int x = 10; x <= 14; x++) world.SetBlock(x, Ground, 8, Shapes.Make(Planks(), Shape.Floor, 0));
            var position = new Vector3(8.5f, Ground, 8.5f);

            for (int i = 0; i < 40; i++)
            {
                position = VoxelCollision.Move(world, position, HalfWidth, Height, new Vector3(0.1f, -0.05f, 0f)).Position;
            }

            Assert.AreEqual(12.5f, position.X, Tolerance);
            Assert.AreEqual(Ground + 0.5f, position.Y, Tolerance);
        }

        [Test]
        public void UnderAFlatRoof_ThereIsRoomToWalk()
        {
            World world = FlatGenerator.CreateWorld();
            for (int x = 10; x <= 14; x++) world.SetBlock(x, Ground + 1, 8, Shapes.Make(Planks(), Shape.FlatRoof, 0));
            for (int x = 10; x <= 14; x++) world.SetBlock(x, Ground + 1, 10, Shapes.Make(Planks(), Shape.Floor, 0));

            MoveResult underRoof = VoxelCollision.Move(world, new Vector3(8.5f, Ground, 8.5f), HalfWidth, 1.45f, new Vector3(4f, 0f, 0f));
            MoveResult underFloor = VoxelCollision.Move(world, new Vector3(8.5f, Ground, 10.5f), HalfWidth, 1.45f, new Vector3(4f, 0f, 0f));

            Assert.IsFalse(underRoof.HitWall, "a flat roof fills the top half of its cell");
            Assert.IsTrue(underFloor.HitWall, "a floor its bottom half");
        }

        [Test]
        public void AimingDownAtAFloor_HitsItsTop()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(8, Ground, 8, Shapes.Make(Planks(), Shape.Floor, 0));

            Assert.IsTrue(VoxelRaycast.Cast(world, new Vector3(8.5f, Ground + 3f, 8.5f), new Vector3(0f, -1f, 0f), 10f, out VoxelHit hit));
            Assert.AreEqual(new BlockPos(8, Ground, 8), hit.Block);
            Assert.AreEqual(new BlockPos(0, 1, 0), hit.Normal);
            Assert.AreEqual(2.5f, hit.Distance, 0.001f);

            bool any = VoxelRaycast.Cast(world, new Vector3(8.5f, Ground + 0.75f, 5.5f), new Vector3(0f, 0f, 1f), 10f, out VoxelHit over);
            Assert.IsFalse(any && over.Block == new BlockPos(8, Ground, 8), "a ray over the slab passes it");
            Assert.IsTrue(VoxelRaycast.Cast(world, new Vector3(8.5f, Ground + 0.25f, 5.5f), new Vector3(0f, 0f, 1f), 10f, out VoxelHit side));
            Assert.AreEqual(new BlockPos(8, Ground, 8), side.Block);
            Assert.AreEqual(new BlockPos(0, 0, -1), side.Normal);
        }

        [Test]
        public void AFlatRoof_DarkensWhatIsUnderIt_AFenceDoesNot()
        {
            var world = new World(2, 2, 2);
            world.Generate(new FlatGenerator(), 0);
            for (int z = 10; z <= 16; z++)
            {
                for (int x = 10; x <= 16; x++)
                {
                    world.SetBlock(x, 12, z, Shapes.Make(Planks(), Shape.FlatRoof, 0));
                    world.SetBlock(x + 12, 12, z, Shapes.Make(Planks(), Shape.Fence, 0));
                }
            }

            var light = new SkyLight(world);

            Assert.AreEqual(SkyLight.Max - 4, light.Get(13, Ground, 13), "as under a roof of full blocks");
            Assert.AreEqual(0, light.Get(13, 12, 13), "a slab holds the light");
            Assert.AreEqual(SkyLight.Max - 1, light.Get(25, 12, 13), "light goes through a fence");
        }

        // A shaped block counts by its boxes: a wall at the far edge of the cell beside a player is not in them.
        [Test]
        public void ABlockInsideAPlayer_IsJudgedByItsBoxes()
        {
            World world = FlatGenerator.CreateWorld();
            var feet = new Vector3(8.5f, Ground, 8.9f);
            var cell = new BlockPos(8, Ground, 9);

            Assert.IsTrue(VoxelCollision.IntersectsBlock(world, feet, HalfWidth, Height, cell, Planks()));
            Assert.IsFalse(VoxelCollision.IntersectsBlock(world, feet, HalfWidth, Height, cell, Shapes.Make(Planks(), Shape.Wall, 0)));
            Assert.IsTrue(VoxelCollision.IntersectsBlock(world, feet, HalfWidth, Height, cell, Shapes.Make(Planks(), Shape.Wall, 2)));
        }
    }
}
