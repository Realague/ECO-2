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
        Ladder,
        CornerPost,
        Beam,
        EdgeBeam,
        HalfWall,
        LowWall,
        Column,
        ColumnBase,
        ColumnTop
    }

    // What the hammer offers (BuildingHud's shapes), each with its rotations: the shapes and the quarter
    // turns, from the way the player looks, it takes in turn (Shapes.RotationOf).
    public enum ShapeFamily : byte
    {
        Cube,
        Wall,
        Post,
        Slab,
        Slope,
        OuterCorner,
        InnerCorner,
        Stairs,
        Fence,
        LowWall,
        Column,
        Ladder
    }

    // A wood block's shape, held in its variant and orientation (Blocks.VariantOf / OrientationOf).
    // Variant 0: 0 a full block, 1 to 4 a wall against the edge of its facing, 5 a post, 6 a floor
    // (the lower half), 7 a flat roof (the upper half), 8 to 11 a sloped roof rising toward its
    // facing, 12 a fence of earlier versions (no axis: a lone one is a bare post). Variant 1, four
    // facings each: 0 to 3 stairs (two half steps rising toward the facing), 4 to 7 a roof's outer
    // corner (rising toward the corner ahead and to the right of the facing: a quarter of a pyramid),
    // 8 to 11 a roof's inner corner (rising toward the two edges about that corner: a valley), 12 to
    // 15 a ladder against the edge of its facing. Variant 2: 0 to 3 a post in the corner ahead and to
    // the right of its facing, 4 to 7 a beam along the top of the edge of its facing, 8 to 11 an
    // upright slab (half the cell, against the edge of its facing), 12 and 13 a beam through the
    // middle of the cell across its facing (facing 0 or 1: along x or z), 14 and 15 a fence whose
    // axis is that way (a lone fence shows its rails along it). Variant 3: 0 and 1 a low wall (a
    // muret) by axis like the fence, 2 a column's shaft, 3 its base, 4 its top. Facing is a quarter
    // turn as StorageRules.FacingOf gives it (0 toward +z, 1 +x, 2 -z, 3 -x), the way the player who
    // placed it looked, turned by the rotation chosen (Place); corners take it from the diagonal
    // looked along (CornerFacingOf). A shaped block is solid (it is placed, stands in its cell, stops
    // rays) but only a full block is opaque; its boxes (GetBoxes) are what collision and aiming meet.
    public static class Shapes
    {
        // The last orientation of variant 0 (variants 1 and 2 take all 16, variant 3 up to LastOfVariant3).
        public const int LastOrientation = 12;

        // How thick a wall is, how wide a post or a fence's post, how high a fence or a low wall stops
        // players, how far a ladder stands out from its wall, how high a low wall's sides and how wide
        // a column's shaft.
        public const float WallThickness = 0.25f;
        public const float PostWidth = 0.25f;
        public const float FenceHeight = 1.5f;
        public const float LadderThickness = 0.125f;
        public const float LowWallHeight = 0.8125f;
        public const float LowWallPostWidth = 0.5f;
        public const float LowWallWidth = 0.375f;
        public const float ColumnWidth = 0.625f;

        private const int FirstWall = 1;
        private const int PostCode = 5;
        private const int FloorCode = 6;
        private const int FlatRoofCode = 7;
        private const int FirstSlope = 8;
        private const int LegacyFenceCode = 12;

        // Variant 1: a family of four facings each, in this order.
        private const int FacingVariant = 1;
        private static readonly Shape[] FacingFamilies = { Shape.Stairs, Shape.OuterCorner, Shape.InnerCorner, Shape.Ladder };

        // Variant 2: corner posts, edge beams, upright slabs (four facings each), then beams and fences
        // by axis.
        private const int MoreVariant = 2;
        private const int FirstCornerPost = 0;
        private const int FirstEdgeBeam = 4;
        private const int FirstHalfWall = 8;
        private const int FirstBeam = 12;
        private const int FirstFence = 14;

        // Variant 3: low walls by axis, then the column's three parts.
        private const int LastVariant = 3;
        private const int FirstLowWall = 0;
        private const int ColumnCode = 2;
        private const int ColumnBaseCode = 3;
        private const int ColumnTopCode = 4;
        private const int LastOfVariant3 = ColumnTopCode;

        // The sides a fence or a low wall joins (ConnectionsOf), one bit each: +z, -z, +x, -x.
        public const int TowardPlusZ = 1, TowardMinusZ = 2, TowardPlusX = 4, TowardMinusX = 8;
        private static readonly (int dx, int dz, int bit)[] Sides = { (0, 1, TowardPlusZ), (0, -1, TowardMinusZ), (1, 0, TowardPlusX), (-1, 0, TowardMinusX) };

        // The hammer's shapes, in the order it cycles through them.
        public static readonly ShapeFamily[] Families =
        {
            ShapeFamily.Cube, ShapeFamily.Wall, ShapeFamily.Post, ShapeFamily.Slab, ShapeFamily.Slope,
            ShapeFamily.OuterCorner, ShapeFamily.InnerCorner, ShapeFamily.Stairs, ShapeFamily.Fence,
            ShapeFamily.LowWall, ShapeFamily.Column, ShapeFamily.Ladder
        };

        // Each family's rotations: a shape and the quarter turns added to the way the player looks.
        // A post: in the middle, then in the corners (far left, far right, near right, near left), then
        // lying: across the view and along it through the middle, and along the top of the far, right,
        // near and left edges. A slab: the upper half, the lower half, then upright against the far,
        // right, near and left edges. A column: its shaft, its base, its top.
        private static readonly (Shape shape, int turns)[][] Rotations =
        {
            new[] { (Shape.Cube, 0) },
            Quarter(Shape.Wall),
            new[]
            {
                (Shape.Post, 0), (Shape.CornerPost, 3), (Shape.CornerPost, 0), (Shape.CornerPost, 1), (Shape.CornerPost, 2),
                (Shape.Beam, 0), (Shape.Beam, 1), (Shape.EdgeBeam, 0), (Shape.EdgeBeam, 1), (Shape.EdgeBeam, 2), (Shape.EdgeBeam, 3)
            },
            new[]
            {
                (Shape.FlatRoof, 0), (Shape.Floor, 0), (Shape.HalfWall, 0), (Shape.HalfWall, 1), (Shape.HalfWall, 2), (Shape.HalfWall, 3)
            },
            Quarter(Shape.Slope),
            Quarter(Shape.OuterCorner),
            Quarter(Shape.InnerCorner),
            Quarter(Shape.Stairs),
            new[] { (Shape.Fence, 0), (Shape.Fence, 1) },
            new[] { (Shape.LowWall, 0), (Shape.LowWall, 1) },
            new[] { (Shape.Column, 0), (Shape.ColumnBase, 0), (Shape.ColumnTop, 0) },
            Quarter(Shape.Ladder)
        };

        private static (Shape, int)[] Quarter(Shape shape) => new[] { (shape, 0), (shape, 1), (shape, 2), (shape, 3) };

        public static int RotationCount(ShapeFamily family) => Rotations[(int)family].Length;

        // A family's rotation (wrapped): its shape and the quarter turns from the way the player looks.
        public static (Shape shape, int turns) RotationOf(ShapeFamily family, int rotation)
        {
            (Shape, int)[] rotations = Rotations[(int)family];
            return rotations[((rotation % rotations.Length) + rotations.Length) % rotations.Length];
        }

        // The family a shape is one of the rotations of.
        public static ShapeFamily FamilyOf(Shape shape)
        {
            for (int family = 0; family < Rotations.Length; family++)
            {
                foreach ((Shape candidate, int _) in Rotations[family])
                {
                    if (candidate == shape) return (ShapeFamily)family;
                }
            }

            return ShapeFamily.Cube;
        }

        // The block a player looking along yaw (degrees) places: a wood block in a family's rotation,
        // turned from the way they look (a roof's corner, from the diagonal they look along).
        public static ushort Place(ushort block, ShapeFamily family, int rotation, float yawDegrees)
        {
            if (!HasShapes(block)) return block;

            (Shape shape, int turns) = RotationOf(family, rotation);
            int look = IsCorner(shape) ? CornerFacingOf(yawDegrees) : StorageRules.FacingOf(yawDegrees);
            return Make(block, shape, (look + turns) & 3);
        }

        public static bool HasShapes(ushort block) => WoodBlocks.IsWoodBlock(block);

        // Whether a wood block's variant and orientation name a shape.
        public static bool IsValidCode(ushort block)
        {
            int variant = Blocks.VariantOf(block);
            int code = Blocks.OrientationOf(block);
            switch (variant)
            {
                case 0: return code <= LastOrientation;
                case FacingVariant:
                case MoreVariant: return true;
                case LastVariant: return code <= LastOfVariant3;
                default: return false;
            }
        }

        // Whether a shape turns with its facing, four ways (the others look the same whichever way they
        // face, or turn by axis: HasAxis).
        public static bool HasFacing(Shape shape) =>
            shape == Shape.Wall || shape == Shape.Slope || shape == Shape.CornerPost || shape == Shape.EdgeBeam
            || shape == Shape.HalfWall || Array.IndexOf(FacingFamilies, shape) >= 0;

        // Whether a shape lies along an axis (FacingOf is then 0 along x or 1 along z).
        public static bool HasAxis(Shape shape) => shape == Shape.Beam || shape == Shape.Fence || shape == Shape.LowWall;

        // Whether a shape is a roof's corner, whose facing comes from the diagonal looked along.
        public static bool IsCorner(Shape shape) => shape == Shape.OuterCorner || shape == Shape.InnerCorner;

        // Whether a shape joins its neighbours (ConnectionsOf).
        public static bool Connects(Shape shape) => shape == Shape.Fence || shape == Shape.LowWall;

        // Whether a block is a fence or a low wall: they join the ground beside them, which then stays
        // a cube on smoothed terrain (SmoothTerrain.IsPinned, counted per chunk: Chunk.HoldsJoins).
        public static bool JoinsGround(ushort block) => HasShapes(block) && Connects(ShapeOf(block));

        // The facing of a corner placed by a player looking along yaw (degrees, 0 toward +z, 90 toward
        // +x): the corner it rises toward is the one looked at (facing 0: +x +z, 1: +x -z, 2: -x -z, 3: -x +z).
        public static int CornerFacingOf(float yawDegrees) => StorageRules.FacingOf(yawDegrees - 45f);

        // A wood block in a shape, facing a way (an axis for a beam, a fence or a low wall: its facing's).
        public static ushort Make(ushort block, Shape shape, int facing)
        {
            int family = Array.IndexOf(FacingFamilies, shape);
            if (family >= 0) return Blocks.WithVariant(block, FacingVariant, family * 4 + (facing & 3));

            switch (shape)
            {
                case Shape.Wall: return Blocks.WithVariant(block, 0, FirstWall + (facing & 3));
                case Shape.Post: return Blocks.WithVariant(block, 0, PostCode);
                case Shape.Floor: return Blocks.WithVariant(block, 0, FloorCode);
                case Shape.FlatRoof: return Blocks.WithVariant(block, 0, FlatRoofCode);
                case Shape.Slope: return Blocks.WithVariant(block, 0, FirstSlope + (facing & 3));
                case Shape.CornerPost: return Blocks.WithVariant(block, MoreVariant, FirstCornerPost + (facing & 3));
                case Shape.EdgeBeam: return Blocks.WithVariant(block, MoreVariant, FirstEdgeBeam + (facing & 3));
                case Shape.HalfWall: return Blocks.WithVariant(block, MoreVariant, FirstHalfWall + (facing & 3));
                case Shape.Beam: return Blocks.WithVariant(block, MoreVariant, FirstBeam + (facing & 1));
                case Shape.Fence: return Blocks.WithVariant(block, MoreVariant, FirstFence + (facing & 1));
                case Shape.LowWall: return Blocks.WithVariant(block, LastVariant, FirstLowWall + (facing & 1));
                case Shape.Column: return Blocks.WithVariant(block, LastVariant, ColumnCode);
                case Shape.ColumnBase: return Blocks.WithVariant(block, LastVariant, ColumnBaseCode);
                case Shape.ColumnTop: return Blocks.WithVariant(block, LastVariant, ColumnTopCode);
                default: return Blocks.WithVariant(block, 0, 0);
            }
        }

        public static Shape ShapeOf(ushort block)
        {
            if (!HasShapes(block)) return Shape.Cube;

            int code = Blocks.OrientationOf(block);
            switch (Blocks.VariantOf(block))
            {
                case FacingVariant:
                    return FacingFamilies[code >> 2];
                case MoreVariant:
                    if (code >= FirstFence) return Shape.Fence;
                    if (code >= FirstBeam) return Shape.Beam;
                    if (code >= FirstHalfWall) return Shape.HalfWall;
                    return code >= FirstEdgeBeam ? Shape.EdgeBeam : Shape.CornerPost;
                case LastVariant:
                    switch (code)
                    {
                        case ColumnCode: return Shape.Column;
                        case ColumnBaseCode: return Shape.ColumnBase;
                        case ColumnTopCode: return Shape.ColumnTop;
                        default: return code <= FirstLowWall + 1 ? Shape.LowWall : Shape.Cube;
                    }
            }

            if (code >= FirstWall && code < FirstWall + 4) return Shape.Wall;
            if (code >= FirstSlope && code < FirstSlope + 4) return Shape.Slope;
            switch (code)
            {
                case PostCode: return Shape.Post;
                case FloorCode: return Shape.Floor;
                case FlatRoofCode: return Shape.FlatRoof;
                case LegacyFenceCode: return Shape.Fence;
                default: return Shape.Cube;
            }
        }

        // The way a shaped block faces (0 to 3), or its axis (0 along x, 1 along z) for a beam, a fence
        // or a low wall; 0 for the others.
        public static int FacingOf(ushort block)
        {
            if (!HasShapes(block)) return 0;

            int code = Blocks.OrientationOf(block);
            switch (Blocks.VariantOf(block))
            {
                case FacingVariant: return code & 3;
                case MoreVariant: return code >= FirstBeam ? code & 1 : code & 3;
                case LastVariant: return code <= FirstLowWall + 1 ? code & 1 : 0;
            }

            if (code >= FirstWall && code < FirstWall + 4) return code - FirstWall;
            return code >= FirstSlope && code < FirstSlope + 4 ? code - FirstSlope : 0;
        }

        // Whether a fence or a low wall has an axis (those of earlier versions have none).
        public static bool HasAxis(ushort block) => HasAxis(ShapeOf(block)) && !(Blocks.VariantOf(block) == 0 && Blocks.OrientationOf(block) == LegacyFenceCode);

        // Whether a block fills its cell: anything but a shaped wood block (a full one does).
        public static bool IsFullCube(ushort block) => ShapeOf(block) == Shape.Cube;

        public static bool IsShaped(ushort block) => !IsFullCube(block);

        // Shapes that stop light like a full block: walls, slabs, roofs and stairs (posts, beams,
        // fences, low walls, columns and ladders let it by).
        public static bool BlocksLight(ushort block)
        {
            switch (ShapeOf(block))
            {
                case Shape.Post:
                case Shape.CornerPost:
                case Shape.Beam:
                case Shape.EdgeBeam:
                case Shape.Fence:
                case Shape.LowWall:
                case Shape.Column:
                case Shape.ColumnBase:
                case Shape.ColumnTop:
                case Shape.Ladder:
                    return false;
                default:
                    return true;
            }
        }

        // Shapes a land animal cannot cross on top of a column: all but the full block, the floor, the
        // flat roof, slopes, stairs and the roof's corners, which are ground.
        public static bool StopsAnimals(ushort block)
        {
            switch (ShapeOf(block))
            {
                case Shape.Cube:
                case Shape.Floor:
                case Shape.FlatRoof:
                case Shape.Slope:
                case Shape.Stairs:
                case Shape.OuterCorner:
                case Shape.InnerCorner:
                    return false;
                default:
                    return true;
            }
        }

        // What a shaped block (block) at pos makes of its neighbours, worked out from them as it is drawn
        // and collided with, never stored (ConnectionsOf):
        // - a fence or a low wall: the sides it joins, one bit each (TowardPlusZ…): fences, low walls,
        //   full blocks, and walls or upright slabs standing against the side they share; one joining
        //   none lies along its axis, if it has one;
        // - a wall: the other ways it also stands against, one bit per facing (1 << facing), making an L
        //   where a wall meets it at an inside corner: the wall in front of it (on its open side) facing
        //   across it, so a room's walls, laid against the outer edges of its cells, close their corners;
        // - stairs: a corner, as other stairs beside them turn (StairsOuter or StairsInner, plus the turn
        //   from its facing to the side the corner turns toward, 1 or 3), or 0 for a straight flight:
        //   stairs behind their high side facing across them make an outer corner (only the quarter
        //   between both high sides stays high), stairs before their low side facing across them an
        //   inner corner (the high half and the quarter beside it), unless stairs facing their way stand
        //   on that side already (they carry the flight on, straight).
        public static int ConnectionsOf(World world, BlockPos pos, ushort block)
        {
            Shape shape = ShapeOf(block);
            switch (shape)
            {
                case Shape.Wall: return WallCorners(world, pos, FacingOf(block));
                case Shape.Stairs: return StairsCorner(world, pos, FacingOf(block));
            }

            if (!Connects(shape)) return 0;

            int mask = 0;
            foreach ((int dx, int dz, int bit) in Sides)
            {
                int x = pos.X + dx;
                int z = pos.Z + dz;
                if (world.InBounds(x, pos.Y, z) && Joins(world.GetBlock(x, pos.Y, z), FacingToward(dx, dz))) mask |= bit;
            }

            if (mask == 0 && HasAxis(block)) mask = AxisMask(FacingOf(block));
            return mask;
        }

        // Stairs' corners (ConnectionsOf): an outer or an inner one, plus the turn to the side it turns toward.
        public const int StairsOuter = 4, StairsInner = 8;

        // The two sides of an axis (0 along x, 1 along z).
        public static int AxisMask(int axis) => (axis & 1) == 0 ? TowardPlusX | TowardMinusX : TowardPlusZ | TowardMinusZ;

        // Whether a fence or a low wall joins its neighbour that lies the way of facing toward.
        private static bool Joins(ushort neighbour, int toward)
        {
            if (!HasShapes(neighbour)) return Blocks.IsOpaque(neighbour);

            Shape other = ShapeOf(neighbour);
            if (other == Shape.Fence || other == Shape.LowWall || other == Shape.Cube) return true;
            // A wall or an upright slab against the side they share: it faces back toward us.
            return (other == Shape.Wall || other == Shape.HalfWall) && FacingOf(neighbour) == ((toward + 2) & 3);
        }

        // The facing (0 +z, 1 +x, 2 -z, 3 -x) of a step to a side.
        private static int FacingToward(int dx, int dz) => dz > 0 ? 0 : dx > 0 ? 1 : dz < 0 ? 2 : 3;

        private static (int dx, int dz) StepOf(int facing)
        {
            switch (facing & 3)
            {
                case 0: return (0, 1);
                case 1: return (1, 0);
                case 2: return (0, -1);
                default: return (-1, 0);
            }
        }

        // The shape and facing of the block a step away (dx, dz) from pos, if it is a shaped wood block.
        private static bool TryShapeAt(World world, BlockPos pos, int dx, int dz, out Shape shape, out int facing)
        {
            shape = Shape.Cube;
            facing = 0;
            int x = pos.X + dx, z = pos.Z + dz;
            if (!world.InBounds(x, pos.Y, z)) return false;

            ushort block = world.GetBlock(x, pos.Y, z);
            if (!HasShapes(block)) return false;

            shape = ShapeOf(block);
            facing = FacingOf(block);
            return true;
        }

        private static int WallCorners(World world, BlockPos pos, int facing)
        {
            (int dx, int dz) = StepOf(facing);
            if (TryShapeAt(world, pos, -dx, -dz, out Shape front, out int across) && front == Shape.Wall && ((across - facing) & 1) == 1) return 1 << across;
            return 0;
        }

        private static int StairsCorner(World world, BlockPos pos, int facing)
        {
            (int dx, int dz) = StepOf(facing);
            if (TryShapeAt(world, pos, dx, dz, out Shape back, out int backFacing) && back == Shape.Stairs && ((backFacing - facing) & 1) == 1
                && !IsStairsFacing(world, pos, backFacing + 2, facing))
            {
                return StairsOuter | ((backFacing - facing) & 3);
            }

            if (TryShapeAt(world, pos, -dx, -dz, out Shape front, out int frontFacing) && front == Shape.Stairs && ((frontFacing - facing) & 1) == 1
                && !IsStairsFacing(world, pos, frontFacing, facing))
            {
                return StairsInner | ((frontFacing - facing) & 3);
            }

            return 0;
        }

        // Whether the neighbour the way of side holds stairs facing facing.
        private static bool IsStairsFacing(World world, BlockPos pos, int side, int facing)
        {
            (int dx, int dz) = StepOf(side);
            return TryShapeAt(world, pos, dx, dz, out Shape shape, out int its) && shape == Shape.Stairs && its == facing;
        }

        // Whether a fence or a low wall (block) at pos reaches toward its neighbour that way.
        public static bool ConnectsToward(World world, BlockPos pos, ushort block, int dx, int dz)
        {
            if (!Connects(ShapeOf(block))) return false;

            int mask = ConnectionsOf(world, pos, block);
            foreach ((int sx, int sz, int bit) in Sides)
            {
                if (sx == dx && sz == dz) return (mask & bit) != 0;
            }

            return false;
        }

        // The boxes of a shaped block at pos (its cell's corner), in world coordinates, added to into;
        // a full block gives its cell. Sloped shapes are steps of a quarter block. For collision a fence
        // or a low wall stands FenceHeight high, else it stays within its cell, as every box does for
        // aiming.
        public static void GetBoxes(World world, BlockPos pos, ushort block, List<(Vector3 min, Vector3 max)> into, bool forCollision = false)
        {
            int first = into.Count;
            GetLocalBoxes(ShapeOf(block), FacingOf(block), ConnectionsOf(world, pos, block), into, forCollision);
            var corner = new Vector3(pos.X, pos.Y, pos.Z);
            for (int i = first; i < into.Count; i++) into[i] = (corner + into[i].min, corner + into[i].max);
        }

        // The boxes of a shape facing a way (or along an axis) in its cell (0 to 1), a fence's or a low
        // wall's toward the sides of connections (ConnectionsOf).
        public static void GetLocalBoxes(Shape shape, int facing, int connections, List<(Vector3 min, Vector3 max)> into, bool forCollision = false)
        {
            switch (shape)
            {
                case Shape.Wall:
                    // Its own, then that of the corner it closes (connections: one bit per facing), short of
                    // its own so that no faces lie on each other.
                    into.Add(Turned(new Vector3(0f, 0f, 1f - WallThickness), new Vector3(1f, 1f, 1f), facing));
                    for (int other = 0; other < 4; other++)
                    {
                        if (((other - facing) & 1) == 0 || (connections & (1 << other)) == 0) continue;

                        bool ownOnRight = ((facing - other) & 3) == 1;
                        into.Add(Turned(new Vector3(ownOnRight ? 0f : WallThickness, 0f, 1f - WallThickness), new Vector3(ownOnRight ? 1f - WallThickness : 1f, 1f, 1f), other));
                    }

                    break;
                case Shape.Post:
                    into.Add(Square(PostWidth, 0f, 1f));
                    break;
                case Shape.CornerPost:
                    into.Add(Turned(new Vector3(1f - PostWidth, 0f, 1f - PostWidth), Vector3.One, facing));
                    break;
                case Shape.Beam:
                    float low = 0.5f - PostWidth * 0.5f, high = 0.5f + PostWidth * 0.5f;
                    into.Add(Turned(new Vector3(0f, low, low), new Vector3(1f, high, high), facing & 1));
                    break;
                case Shape.EdgeBeam:
                    into.Add(Turned(new Vector3(0f, 1f - PostWidth, 1f - PostWidth), Vector3.One, facing));
                    break;
                case Shape.Floor:
                    into.Add((Vector3.Zero, new Vector3(1f, 0.5f, 1f)));
                    break;
                case Shape.FlatRoof:
                    into.Add((new Vector3(0f, 0.5f, 0f), Vector3.One));
                    break;
                case Shape.HalfWall:
                    into.Add(Turned(new Vector3(0f, 0f, 0.5f), Vector3.One, facing));
                    break;
                case Shape.Slope:
                    // Four steps, each a quarter higher and a quarter shorter, rising toward the facing.
                    for (int step = 0; step < 4; step++)
                    {
                        into.Add(Turned(new Vector3(0f, step * 0.25f, step * 0.25f), new Vector3(1f, (step + 1) * 0.25f, 1f), facing));
                    }

                    break;
                case Shape.Stairs:
                    into.Add((Vector3.Zero, new Vector3(1f, 0.5f, 1f)));
                    AddStairsTop(into, facing, connections);
                    break;
                case Shape.OuterCorner:
                    // Each step a quarter shorter both ways: what is left rises toward the corner (1, 1).
                    for (int step = 0; step < 4; step++)
                    {
                        float s = step * 0.25f;
                        into.Add(Turned(new Vector3(s, s, s), new Vector3(1f, s + 0.25f, 1f), facing));
                    }

                    break;
                case Shape.InnerCorner:
                    // Each step leaves the strip along the two far edges (x = 1 and z = 1).
                    into.Add((Vector3.Zero, new Vector3(1f, 0.25f, 1f)));
                    for (int step = 1; step < 4; step++)
                    {
                        float s = step * 0.25f;
                        into.Add(Turned(new Vector3(0f, s, s), new Vector3(1f, s + 0.25f, 1f), facing));
                        into.Add(Turned(new Vector3(s, s, 0f), new Vector3(1f, s + 0.25f, s), facing));
                    }

                    break;
                case Shape.Ladder:
                    into.Add(Turned(new Vector3(0.1f, 0f, 1f - LadderThickness), new Vector3(0.9f, 1f, 1f), facing));
                    break;
                case Shape.Fence:
                    float top = forCollision ? FenceHeight : 1f;
                    into.Add(Square(PostWidth, 0f, top));
                    AddArms(into, connections, PostWidth, PostWidth, top);
                    break;
                case Shape.LowWall:
                    AddLowWall(into, connections, forCollision);
                    break;
                case Shape.Column:
                    into.Add(Square(ColumnWidth, 0f, 1f));
                    break;
                case Shape.ColumnBase:
                    into.Add(Square(0.875f, 0f, 0.1875f));
                    into.Add(Square(0.75f, 0.1875f, 0.3125f));
                    into.Add(Square(ColumnWidth, 0.3125f, 1f));
                    break;
                case Shape.ColumnTop:
                    into.Add(Square(ColumnWidth, 0f, 0.6875f));
                    into.Add(Square(0.75f, 0.6875f, 0.8125f));
                    into.Add(Square(0.875f, 0.8125f, 1f));
                    break;
                default:
                    into.Add((Vector3.Zero, Vector3.One));
                    break;
            }
        }

        // Stairs' upper step, given facing +z then turned: the back half; at an outer corner only its
        // quarter on the side the corner turns toward (x 1 for a turn of 1, x 0 for 3), at an inner corner
        // the back half and the front quarter on that side.
        private static void AddStairsTop(List<(Vector3 min, Vector3 max)> into, int facing, int corner)
        {
            bool right = (corner & 3) == 1;
            float x0 = right ? 0.5f : 0f, x1 = right ? 1f : 0.5f;
            if ((corner & StairsOuter) != 0)
            {
                into.Add(Turned(new Vector3(x0, 0.5f, 0.5f), Vector3.One, facing));
                return;
            }

            into.Add(Turned(new Vector3(0f, 0.5f, 0.5f), Vector3.One, facing));
            if ((corner & StairsInner) != 0) into.Add(Turned(new Vector3(x0, 0.5f, 0f), new Vector3(x1, 1f, 0.5f), facing));
        }

        // A low wall: a post with sides toward what it joins, or, joining two opposite sides only, one
        // straight length without its post.
        private static void AddLowWall(List<(Vector3 min, Vector3 max)> into, int connections, bool forCollision)
        {
            float postTop = forCollision ? FenceHeight : 1f;
            float sideTop = forCollision ? FenceHeight : LowWallHeight;
            float half = LowWallWidth * 0.5f;
            if (connections == (TowardPlusX | TowardMinusX))
            {
                into.Add((new Vector3(0f, 0f, 0.5f - half), new Vector3(1f, sideTop, 0.5f + half)));
                return;
            }

            if (connections == (TowardPlusZ | TowardMinusZ))
            {
                into.Add((new Vector3(0.5f - half, 0f, 0f), new Vector3(0.5f + half, sideTop, 1f)));
                return;
            }

            into.Add(Square(LowWallPostWidth, 0f, postTop));
            AddArms(into, connections, LowWallPostWidth, LowWallWidth, sideTop);
        }

        // An arm from a post (postWidth wide, in the middle) to each side of connections, width wide.
        private static void AddArms(List<(Vector3 min, Vector3 max)> into, int connections, float postWidth, float width, float top)
        {
            float edge = postWidth * 0.5f;
            float half = width * 0.5f;
            if ((connections & TowardPlusZ) != 0) into.Add((new Vector3(0.5f - half, 0f, 0.5f + edge), new Vector3(0.5f + half, top, 1f)));
            if ((connections & TowardMinusZ) != 0) into.Add((new Vector3(0.5f - half, 0f, 0f), new Vector3(0.5f + half, top, 0.5f - edge)));
            if ((connections & TowardPlusX) != 0) into.Add((new Vector3(0.5f + edge, 0f, 0.5f - half), new Vector3(1f, top, 0.5f + half)));
            if ((connections & TowardMinusX) != 0) into.Add((new Vector3(0f, 0f, 0.5f - half), new Vector3(0.5f - edge, top, 0.5f + half)));
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

        // A box width wide in the middle of its cell, from bottom to top.
        private static (Vector3 min, Vector3 max) Square(float width, float bottom, float top)
        {
            float half = width * 0.5f;
            return (new Vector3(0.5f - half, bottom, 0.5f - half), new Vector3(0.5f + half, top, 0.5f + half));
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
