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
            foreach (Shape shape in (Shape[])System.Enum.GetValues(typeof(Shape)))
            {
                for (int facing = 0; facing < 4; facing++)
                {
                    ushort block = Shapes.Make(planks, shape, facing);
                    Assert.AreEqual(shape, Shapes.ShapeOf(block));
                    Assert.IsTrue(Blocks.IsValid(block), $"{shape} {facing}");
                    Assert.IsTrue(Blocks.IsSolid(block) && Blocks.IsPlaceable(block) && Blocks.IsConstruction(block));
                    Assert.AreEqual((int)planks, Items.DropOf(block), "a shape takes and gives back one block");
                    if (Shapes.HasFacing(shape)) Assert.AreEqual(facing, Shapes.FacingOf(block));
                    if (Shapes.HasAxis(shape)) Assert.AreEqual(facing & 1, Shapes.FacingOf(block));
                }
            }

            Assert.IsFalse(Blocks.IsValid(Blocks.WithOrientation(planks, Shapes.LastOrientation + 1)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(planks, 3, 5)), "past the column's top");
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(planks, 4, 0)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithOrientation(Blocks.Stone, 1)), "natural blocks have no shapes");
            Assert.AreEqual(Shape.Cube, Shapes.ShapeOf(Blocks.WithOrientation(Blocks.Torch, 1)), "a torch's orientation is not a shape");

            // The variant bits: wood blocks use only variant 1 (rocks hold their ore there, Ores, and
            // other blocks none); the type reads past them.
            ushort stairs = Shapes.Make(planks, Shape.Stairs, 2);
            Assert.AreEqual(planks, Blocks.TypeOf(stairs));
            Assert.AreEqual(1, Blocks.VariantOf(stairs));
            Assert.IsTrue(WoodBlocks.IsPlanks(stairs));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Dirt, 1, 0)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Stone, Ores.Count + 1, 0)), "past the last ore");
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

        // A fence joins the fences and full blocks beside it with rails, and stands taller for collision
        // than for aiming.
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
            Assert.AreEqual(4, boxes.Count, "a post and three rails, one to the full block");
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

        // Every rotation of every shape the hammer offers places a valid block of that shape.
        [Test]
        public void EveryRotation_PlacesItsShape()
        {
            ushort planks = Planks();
            foreach (ShapeFamily family in Shapes.Families)
            {
                for (int rotation = 0; rotation < Shapes.RotationCount(family); rotation++)
                {
                    foreach (float yaw in new[] { 0f, 90f, 180f, 270f })
                    {
                        ushort block = Shapes.Place(planks, family, rotation, yaw);
                        Assert.IsTrue(Blocks.IsValid(block), $"{family} {rotation} {yaw}");
                        Assert.AreEqual(Shapes.RotationOf(family, rotation).shape, Shapes.ShapeOf(block));
                        Assert.AreEqual(family, Shapes.FamilyOf(Shapes.ShapeOf(block)));
                    }
                }
            }

            Assert.AreEqual(Shapes.RotationOf(ShapeFamily.Wall, 1), Shapes.RotationOf(ShapeFamily.Wall, 5), "rotations wrap");
        }

        // Rotations count from the way the player looks: looking toward +z, a wall turned once stands
        // on the right (+x), a post's second rotation is in the far left corner, a slab's first is the
        // upper half and its third stands upright against the far edge; looking toward +x, the same post
        // is in the far left corner seen from there (+x +z).
        [Test]
        public void Rotations_TurnFromTheWayThePlayerLooks()
        {
            ushort planks = Planks();
            AssertBox(Shapes.Place(planks, ShapeFamily.Wall, 1, 0f), new Vector3(0.75f, 0f, 0f), new Vector3(1f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Post, 1, 0f), new Vector3(0f, 0f, 0.75f), new Vector3(0.25f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Post, 2, 0f), new Vector3(0.75f, 0f, 0.75f), new Vector3(1f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Post, 1, 90f), new Vector3(0.75f, 0f, 0.75f), new Vector3(1f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Post, 5, 0f), new Vector3(0f, 0.375f, 0.375f), new Vector3(1f, 0.625f, 0.625f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Post, 6, 0f), new Vector3(0.375f, 0.375f, 0f), new Vector3(0.625f, 0.625f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Post, 7, 0f), new Vector3(0f, 0.75f, 0.75f), new Vector3(1f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Slab, 0, 0f), new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Slab, 1, 0f), new Vector3(0f, 0f, 0f), new Vector3(1f, 0.5f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Slab, 2, 0f), new Vector3(0f, 0f, 0.5f), new Vector3(1f, 1f, 1f));
            AssertBox(Shapes.Place(planks, ShapeFamily.Slab, 3, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(1f, 1f, 1f));
            Assert.AreEqual(Shapes.Make(planks, Shape.Slope, 3), Shapes.Place(planks, ShapeFamily.Slope, 1, 180f));
        }

        private static void AssertBox(ushort block, Vector3 min, Vector3 max)
        {
            var boxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetBoxes(FlatGenerator.CreateWorld(), new BlockPos(0, 0, 0), block, boxes);
            Assert.AreEqual(1, boxes.Count, Shapes.ShapeOf(block).ToString());
            Assert.AreEqual(min.X, boxes[0].min.X, 1e-5f);
            Assert.AreEqual(min.Y, boxes[0].min.Y, 1e-5f);
            Assert.AreEqual(min.Z, boxes[0].min.Z, 1e-5f);
            Assert.AreEqual(max.X, boxes[0].max.X, 1e-5f);
            Assert.AreEqual(max.Y, boxes[0].max.Y, 1e-5f);
            Assert.AreEqual(max.Z, boxes[0].max.Z, 1e-5f);
        }

        // A fence alone lies along its axis; one joining other fences joins them only. A fence of earlier
        // versions has no axis: alone, it is a bare post.
        [Test]
        public void ALoneFence_LiesAlongItsAxis()
        {
            World world = FlatGenerator.CreateWorld();
            var pos = new BlockPos(5, Ground, 5);
            ushort alongZ = Shapes.Make(Planks(), Shape.Fence, 1);
            Assert.AreEqual(Shapes.TowardPlusZ | Shapes.TowardMinusZ, Shapes.ConnectionsOf(world, pos, alongZ));

            world.SetBlock(6, Ground, 5, Shapes.Make(Planks(), Shape.Fence, 0));
            Assert.AreEqual(Shapes.TowardPlusX, Shapes.ConnectionsOf(world, pos, alongZ));

            ushort legacy = Blocks.WithVariant(Planks(), 0, 12);
            Assert.AreEqual(Shape.Fence, Shapes.ShapeOf(legacy));
            Assert.AreEqual(0, Shapes.ConnectionsOf(world, new BlockPos(9, Ground, 9), legacy));
        }

        // A low wall joins low walls and full blocks; between two, straight, it is one length without its
        // post; it lets light by and stops animals and, for collision, players.
        [Test]
        public void ALowWall_JoinsLowWallsAndBlocks()
        {
            World world = FlatGenerator.CreateWorld();
            ushort wall = Shapes.Make(Planks(), Shape.LowWall, 0);
            world.SetBlock(4, Ground, 5, wall);
            world.SetBlock(6, Ground, 5, Planks());
            var boxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetBoxes(world, new BlockPos(5, Ground, 5), wall, boxes);
            Assert.AreEqual(1, boxes.Count, "straight: no post");
            Assert.AreEqual(Shapes.LowWallHeight, boxes[0].max.Y - Ground, 1e-5f);

            world.SetBlock(5, Ground, 6, wall);
            boxes.Clear();
            Shapes.GetBoxes(world, new BlockPos(5, Ground, 5), wall, boxes, true);
            Assert.AreEqual(4, boxes.Count, "a post and three sides");
            foreach ((Vector3 _, Vector3 max) in boxes) Assert.AreEqual(Ground + Shapes.FenceHeight, max.Y);

            Assert.IsFalse(Blocks.BlocksLight(wall));
            Assert.IsTrue(Shapes.StopsAnimals(wall));
        }

        // A column's base and top are wider at their foot and head, within the cell; light passes.
        [Test]
        public void AColumn_HasABaseAndATop()
        {
            ushort planks = Planks();
            foreach (Shape shape in new[] { Shape.Column, Shape.ColumnBase, Shape.ColumnTop })
            {
                var boxes = new List<(Vector3 min, Vector3 max)>();
                Shapes.GetBoxes(FlatGenerator.CreateWorld(), new BlockPos(0, 0, 0), Shapes.Make(planks, shape, 0), boxes);
                foreach ((Vector3 min, Vector3 max) in boxes)
                {
                    Assert.IsTrue(min.X >= 0f && min.Z >= 0f && max.X <= 1f && max.Z <= 1f && min.Y >= 0f && max.Y <= 1f, shape.ToString());
                }

                Assert.AreEqual(shape == Shape.Column ? 1 : 3, boxes.Count);
                Assert.IsFalse(Blocks.BlocksLight(Shapes.Make(planks, shape, 0)));
            }

            var baseBoxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetLocalBoxes(Shape.ColumnBase, 0, 0, baseBoxes);
            Assert.Greater(baseBoxes[0].max.X - baseBoxes[0].min.X, Shapes.ColumnWidth, "the foot is wider than the shaft");
            Assert.AreEqual(0f, baseBoxes[0].min.Y);
        }

        // A fence or a low wall joins fences, low walls, full blocks (natural ones too) and a wall standing
        // against the side they share, not one turned away.
        [Test]
        public void FencesAndLowWalls_JoinEachOther_BlocksAndWallsFacingThem()
        {
            World world = FlatGenerator.CreateWorld();
            var pos = new BlockPos(5, Ground, 5);
            ushort fence = Shapes.Make(Planks(), Shape.Fence, 0);
            world.SetBlock(pos.X, pos.Y, pos.Z, fence);
            world.SetBlock(6, Ground, 5, Shapes.Make(Planks(), Shape.LowWall, 0));
            world.SetBlock(4, Ground, 5, Blocks.Stone);
            world.SetBlock(5, Ground, 6, Shapes.Make(Planks(), Shape.Wall, 2));
            world.SetBlock(5, Ground, 4, Shapes.Make(Planks(), Shape.Wall, 2));

            Assert.AreEqual(Shapes.TowardPlusX | Shapes.TowardMinusX | Shapes.TowardPlusZ, Shapes.ConnectionsOf(world, pos, fence),
                "the wall at +z stands against the shared side (facing -z), the one at -z against its far side");
            Assert.AreEqual(Shapes.TowardMinusX, Shapes.ConnectionsOf(world, new BlockPos(6, Ground, 5), Shapes.Make(Planks(), Shape.LowWall, 0)),
                "the low wall joins the fence");
        }

        // A room's walls, laid against the outer edges of its cells, close their corners: the corner's
        // wall makes an L with the wall in front of it, without boxes lying on each other.
        [Test]
        public void Walls_CloseTheInsideCorner()
        {
            World world = FlatGenerator.CreateWorld();
            var corner = new BlockPos(5, Ground, 5);
            ushort north = Shapes.Make(Planks(), Shape.Wall, 0);
            world.SetBlock(corner.X, corner.Y, corner.Z, north);
            world.SetBlock(5, Ground, 4, Shapes.Make(Planks(), Shape.Wall, 1));
            world.SetBlock(4, Ground, 5, north);

            Assert.AreEqual(1 << 1, Shapes.ConnectionsOf(world, corner, north));
            var boxes = new List<(Vector3 min, Vector3 max)>();
            Shapes.GetBoxes(world, corner, north, boxes);
            Assert.AreEqual(2, boxes.Count);
            Assert.IsTrue(Covers(boxes, new Vector3(5.9f, Ground + 0.5f, 5.1f)), "the east side, down to the wall in front");
            Assert.IsTrue(Covers(boxes, new Vector3(5.1f, Ground + 0.5f, 5.9f)), "the north side");
            Assert.IsFalse(Covers(boxes, new Vector3(5.5f, Ground + 0.5f, 5.5f)), "the inside stays open");
            float overlapX = System.MathF.Min(boxes[0].max.X, boxes[1].max.X) - System.MathF.Max(boxes[0].min.X, boxes[1].min.X);
            float overlapZ = System.MathF.Min(boxes[0].max.Z, boxes[1].max.Z) - System.MathF.Max(boxes[0].min.Z, boxes[1].min.Z);
            Assert.IsTrue(overlapX <= 1e-5f || overlapZ <= 1e-5f, "the two boxes do not overlap");

            Assert.AreEqual(0, Shapes.ConnectionsOf(world, new BlockPos(4, Ground, 5), north), "a straight run stays straight");
        }

        // Stairs turn as stairs beside them do: an outer corner where stairs behind their high side face
        // across them, an inner one where stairs before their low side do, unless stairs facing their
        // way carry the flight on.
        [Test]
        public void Stairs_TurnAtCorners()
        {
            ushort up = Shapes.Make(Planks(), Shape.Stairs, 0);
            ushort across = Shapes.Make(Planks(), Shape.Stairs, 1);
            var pos = new BlockPos(5, Ground, 5);
            var boxes = new List<(Vector3 min, Vector3 max)>();

            World outer = FlatGenerator.CreateWorld();
            outer.SetBlock(5, Ground, 6, across);
            Assert.AreEqual(Shapes.StairsOuter | 1, Shapes.ConnectionsOf(outer, pos, up));
            Shapes.GetBoxes(outer, pos, up, boxes);
            Assert.IsTrue(Covers(boxes, new Vector3(5.75f, Ground + 0.75f, 5.75f)), "high at the corner between both high sides");
            Assert.IsFalse(Covers(boxes, new Vector3(5.25f, Ground + 0.75f, 5.75f)));
            Assert.IsFalse(Covers(boxes, new Vector3(5.75f, Ground + 0.75f, 5.25f)));

            World inner = FlatGenerator.CreateWorld();
            inner.SetBlock(5, Ground, 4, across);
            Assert.AreEqual(Shapes.StairsInner | 1, Shapes.ConnectionsOf(inner, pos, up));
            boxes.Clear();
            Shapes.GetBoxes(inner, pos, up, boxes);
            Assert.IsTrue(Covers(boxes, new Vector3(5.25f, Ground + 0.75f, 5.75f)), "the high half");
            Assert.IsTrue(Covers(boxes, new Vector3(5.75f, Ground + 0.75f, 5.25f)), "and the quarter beside it");
            Assert.IsFalse(Covers(boxes, new Vector3(5.25f, Ground + 0.75f, 5.25f)), "low in the other quarter");

            // Stairs facing their way on the side the outer corner would leave low: the flight goes on.
            outer.SetBlock(4, Ground, 5, up);
            Assert.AreEqual(0, Shapes.ConnectionsOf(outer, pos, up));
            Assert.AreEqual(0, Shapes.ConnectionsOf(FlatGenerator.CreateWorld(), pos, up), "alone, straight");
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
