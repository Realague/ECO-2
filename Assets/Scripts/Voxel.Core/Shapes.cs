using System;
using System.Collections.Generic;
using System.Numerics;

namespace Voxel.Core
{
    // The shapes a wood block (WoodBlocks: planks, logs) takes when a hammer places it.
    public enum Shape : byte
    {
        Cube,
        Wall,
        Post,
        Floor,
        FlatRoof,
        Slope,
        Fence,
        Stairs,
        OuterCorner,
        InnerCorner,
        Ladder
    }

    // A wood block's shape, held in its variant and orientation (Blocks.VariantOf / OrientationOf).
    // Variant 0: 0 a full block, 1 to 4 a wall against the edge of its facing, 5 a post, 6 a floor
    // (the lower half), 7 a flat roof (the upper half), 8 to 11 a sloped roof rising toward its
    // facing, 12 a fence (a post, with rails toward the fences beside it). Variant 1, four facings
    // each: 0 to 3 stairs (two half steps rising toward the facing), 4 to 7 a roof's outer corner
    // (rising toward the corner ahead and to the right of the facing: a quarter of a pyramid), 8 to
    // 11 a roof's inner corner (rising toward the two edges about that corner: a valley), 12 to 15 a
    // ladder against the edge of its facing. Facing is a quarter turn as StorageRules.FacingOf gives
    // it (0 toward +z, 1 +x, 2 -z, 3 -x), the way the player who placed it looked; corners take it
    // from the diagonal looked along (CornerFacingOf). A shaped block is solid (it is placed, stands
    // in its cell, stops rays) but only a full block is opaque; its boxes (GetBoxes) are what
    // collision and aiming meet.
    public static class Shapes
    {
        // The last orientation of variant 0 (variant 1 takes all 16).
        public const int LastOrientation = 12;

        // How thick a wall is, how wide a post or a fence's post, how high a fence stops players, how
        // far a ladder stands out from its wall.
        public const float WallThickness = 0.25f;
        public const float PostWidth = 0.25f;
        public const float FenceHeight = 1.5f;
        public const float LadderThickness = 0.125f;

        private const int FirstWall = 1;
        private const int PostCode = 5;
        private const int FloorCode = 6;
        private const int FlatRoofCode = 7;
        private const int FirstSlope = 8;
        private const int FenceCode = 12;

        // Variant 1: a family of four facings each, in this order.
        private const int FacingVariant = 1;
        private static readonly Shape[] FacingFamilies = { Shape.Stairs, Shape.OuterCorner, Shape.InnerCorner, Shape.Ladder };

        // The shapes in the order a hammer cycles through them.
        public static readonly Shape[] All =
        {
            Shape.Cube, Shape.Wall, Shape.Post, Shape.Floor, Shape.FlatRoof, Shape.Slope,
            Shape.OuterCorner, Shape.InnerCorner, Shape.Stairs, Shape.Fence, Shape.Ladder
        };

        public static bool HasShapes(ushort block) => WoodBlocks.IsWoodBlock(block);

        // Whether a wood block's variant and orientation name a shape.
        public static bool IsValidCode(ushort block)
        {
            int variant = Blocks.VariantOf(block);
            return variant == FacingVariant || (variant == 0 && Blocks.OrientationOf(block) <= LastOrientation);
        }

        // Whether a shape turns with its facing (the others look the same whichever way they face).
        public static bool HasFacing(Shape shape) =>
            shape == Shape.Wall || shape == Shape.Slope || Array.IndexOf(FacingFamilies, shape) >= 0;

        // Whether a shape is a roof's corner, whose facing comes from the diagonal looked along.
        public static bool IsCorner(Shape shape) => shape == Shape.OuterCorner || shape == Shape.InnerCorner;

        // The facing of a corner placed by a player looking along yaw (degrees, 0 toward +z, 90 toward
        // +x): the corner it rises toward is the one looked at (facing 0: +x +z, 1: +x -z, 2: -x -z, 3: -x +z).
        public static int CornerFacingOf(float yawDegrees) => StorageRules.FacingOf(yawDegrees - 45f);

        // A wood block in a shape, facing a way.
        public static ushort Make(ushort block, Shape shape, int facing)
        {
            int family = Array.IndexOf(FacingFamilies, shape);
            if (family >= 0) return Blocks.WithVariant(block, FacingVariant, family * 4 + (facing & 3));

            int code;
            switch (shape)
            {
                case Shape.Wall: code = FirstWall + (facing & 3); break;
                case Shape.Post: code = PostCode; break;
                case Shape.Floor: code = FloorCode; break;
                case Shape.FlatRoof: code = FlatRoofCode; break;
                case Shape.Slope: code = FirstSlope + (facing & 3); break;
                case Shape.Fence: code = FenceCode; break;
                default: code = 0; break;
            }

            return Blocks.WithVariant(block, 0, code);
        }

        public static Shape ShapeOf(ushort block)
        {
            if (!HasShapes(block)) return Shape.Cube;

            int code = Blocks.OrientationOf(block);
            if (Blocks.VariantOf(block) == FacingVariant) return FacingFamilies[code >> 2];
            if (code >= FirstWall && code < FirstWall + 4) return Shape.Wall;
            if (code >= FirstSlope && code < FirstSlope + 4) return Shape.Slope;
            switch (code)
            {
                case PostCode: return Shape.Post;
                case FloorCode: return Shape.Floor;
                case FlatRoofCode: return Shape.FlatRoof;
                case FenceCode: return Shape.Fence;
                default: return Shape.Cube;
            }
        }

        public static int FacingOf(ushort block)
        {
            int code = Blocks.OrientationOf(block);
            if (HasShapes(block) && Blocks.VariantOf(block) == FacingVariant) return code & 3;
            if (code >= FirstWall && code < FirstWall + 4) return code - FirstWall;
            return code >= FirstSlope && code < FirstSlope + 4 ? code - FirstSlope : 0;
        }

        // Whether a block fills its cell: anything but a shaped wood block (a full one does).
        public static bool IsFullCube(ushort block) => ShapeOf(block) == Shape.Cube;

        public static bool IsShaped(ushort block) => !IsFullCube(block);

        // Shapes that stop light like a full block (a post, a fence and a ladder let it by).
        public static bool BlocksLight(ushort block)
        {
            Shape shape = ShapeOf(block);
            return shape != Shape.Post && shape != Shape.Fence && shape != Shape.Ladder;
        }

        // Shapes a land animal cannot cross: a wall, a post, a fence or a ladder on top of a column.
        public static bool StopsAnimals(ushort block)
        {
            Shape shape = ShapeOf(block);
            return shape == Shape.Wall || shape == Shape.Post || shape == Shape.Fence || shape == Shape.Ladder;
        }

        // Whether a fence at pos reaches toward its neighbour that way (a rail): toward another fence.
        public static bool ConnectsToward(World world, BlockPos pos, int dx, int dz)
        {
            int x = pos.X + dx;
            int z = pos.Z + dz;
            return world.InBounds(x, pos.Y, z) && ShapeOf(world.GetBlock(x, pos.Y, z)) == Shape.Fence;
        }

        // The boxes of a shaped block at pos (its cell's corner), in world coordinates, added to into;
        // a full block gives its cell. Sloped shapes are steps of a quarter block. For collision a fence
        // stands FenceHeight high (and its rails too), else it stays within its cell, as every box does
        // for aiming.
        public static void GetBoxes(World world, BlockPos pos, ushort block, List<(Vector3 min, Vector3 max)> into, bool forCollision = false)
        {
            var corner = new Vector3(pos.X, pos.Y, pos.Z);
            int facing = FacingOf(block);
            switch (ShapeOf(block))
            {
                case Shape.Wall:
                    Add(into, corner, Turned(new Vector3(0f, 0f, 1f - WallThickness), new Vector3(1f, 1f, 1f), facing));
                    break;
                case Shape.Post:
                    Add(into, corner, (Centred(0f, 1f), Centred(0f, 1f, true)));
                    break;
                case Shape.Floor:
                    Add(into, corner, (Vector3.Zero, new Vector3(1f, 0.5f, 1f)));
                    break;
                case Shape.FlatRoof:
                    Add(into, corner, (new Vector3(0f, 0.5f, 0f), Vector3.One));
                    break;
                case Shape.Slope:
                    // Four steps, each a quarter higher and a quarter shorter, rising toward the facing.
                    for (int step = 0; step < 4; step++)
                    {
                        Add(into, corner, Turned(new Vector3(0f, step * 0.25f, step * 0.25f), new Vector3(1f, (step + 1) * 0.25f, 1f), facing));
                    }

                    break;
                case Shape.Stairs:
                    Add(into, corner, (Vector3.Zero, new Vector3(1f, 0.5f, 1f)));
                    Add(into, corner, Turned(new Vector3(0f, 0.5f, 0.5f), Vector3.One, facing));
                    break;
                case Shape.OuterCorner:
                    // Each step a quarter shorter both ways: what is left rises toward the corner (1, 1).
                    for (int step = 0; step < 4; step++)
                    {
                        float s = step * 0.25f;
                        Add(into, corner, Turned(new Vector3(s, s, s), new Vector3(1f, s + 0.25f, 1f), facing));
                    }

                    break;
                case Shape.InnerCorner:
                    // Each step leaves the strip along the two far edges (x = 1 and z = 1).
                    Add(into, corner, (Vector3.Zero, new Vector3(1f, 0.25f, 1f)));
                    for (int step = 1; step < 4; step++)
                    {
                        float s = step * 0.25f;
                        Add(into, corner, Turned(new Vector3(0f, s, s), new Vector3(1f, s + 0.25f, 1f), facing));
                        Add(into, corner, Turned(new Vector3(s, s, 0f), new Vector3(1f, s + 0.25f, s), facing));
                    }

                    break;
                case Shape.Ladder:
                    Add(into, corner, Turned(new Vector3(0.1f, 0f, 1f - LadderThickness), new Vector3(0.9f, 1f, 1f), facing));
                    break;
                case Shape.Fence:
                    float top = forCollision ? FenceHeight : 1f;
                    Add(into, corner, (Centred(0f, top), Centred(0f, top, true)));
                    float half = PostWidth * 0.5f;
                    if (ConnectsToward(world, pos, 0, 1)) Add(into, corner, (new Vector3(0.5f - half, 0f, 0.5f + half), new Vector3(0.5f + half, top, 1f)));
                    if (ConnectsToward(world, pos, 0, -1)) Add(into, corner, (new Vector3(0.5f - half, 0f, 0f), new Vector3(0.5f + half, top, 0.5f - half)));
                    if (ConnectsToward(world, pos, 1, 0)) Add(into, corner, (new Vector3(0.5f + half, 0f, 0.5f - half), new Vector3(1f, top, 0.5f + half)));
                    if (ConnectsToward(world, pos, -1, 0)) Add(into, corner, (new Vector3(0f, 0f, 0.5f - half), new Vector3(0.5f - half, top, 0.5f + half)));
                    break;
                default:
                    into.Add((corner, corner + Vector3.One));
                    break;
            }
        }

        // Whether a box (feet at position, halfWidth, height) touches a ladder, or comes within reach
        // of one: a player there climbs (PlayerController).
        public static bool TouchesLadder(World world, Vector3 position, float halfWidth, float height, float reach = 0.15f)
        {
            float w = halfWidth + reach;
            int minX = (int)MathF.Floor(position.X - w), maxX = (int)MathF.Floor(position.X + w);
            int minY = (int)MathF.Floor(position.Y), maxY = (int)MathF.Floor(position.Y + height);
            int minZ = (int)MathF.Floor(position.Z - w), maxZ = (int)MathF.Floor(position.Z + w);
            var boxes = new List<(Vector3 min, Vector3 max)>();
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        if (!world.InBounds(x, y, z)) continue;

                        ushort block = world.GetBlock(x, y, z);
                        if (ShapeOf(block) != Shape.Ladder) continue;

                        boxes.Clear();
                        GetBoxes(world, new BlockPos(x, y, z), block, boxes);
                        foreach ((Vector3 min, Vector3 max) in boxes)
                        {
                            if (position.X + w > min.X && position.X - w < max.X && position.Z + w > min.Z && position.Z - w < max.Z
                                && position.Y < max.Y && position.Y + height > min.Y) return true;
                        }
                    }
                }
            }

            return false;
        }

        // A post's box, PostWidth wide in the middle of its cell, from bottom to top (max: its far corner).
        private static Vector3 Centred(float bottom, float top, bool max = false)
        {
            float half = PostWidth * 0.5f;
            return max ? new Vector3(0.5f + half, top, 0.5f + half) : new Vector3(0.5f - half, bottom, 0.5f - half);
        }

        private static void Add(List<(Vector3 min, Vector3 max)> into, Vector3 corner, (Vector3 min, Vector3 max) box)
        {
            into.Add((corner + box.min, corner + box.max));
        }

        // A box given facing +z, turned by the facing about the cell's middle, a quarter turn at a
        // time: (x, z) -> (z, -x), as Doors.GetBox.
        public static (Vector3 min, Vector3 max) Turned(Vector3 min, Vector3 max, int facing)
        {
            for (int turn = 0; turn < (facing & 3); turn++)
            {
                float x0 = min.X - 0.5f, x1 = max.X - 0.5f, z0 = min.Z - 0.5f, z1 = max.Z - 0.5f;
                min = new Vector3(z0 + 0.5f, min.Y, -x1 + 0.5f);
                max = new Vector3(z1 + 0.5f, max.Y, -x0 + 0.5f);
            }

            return (min, max);
        }
    }
}
