using System;
using Voxel.Core;

namespace Eco.Sim
{
    // What an animal can stand on, for AnimalMoves. The simulation adds that a column with another
    // animal in it is taken; the client's display only looks at the blocks.
    public interface IAnimalGround
    {
        // Height of the air block an animal would stand in, or false if it cannot stand in this column.
        bool TryGetGround(int x, int z, out int y);

        // False if another animal already stands in this column.
        bool IsFree(int x, int z);
    }

    // Where a species lives: on land (most), in water (fish), on land and floating on water (ducks,
    // otters), on land and in shallow water (herons), or in the air (birds). See AnimalMoves.GroundFor.
    public enum AnimalHabitat
    {
        Land,
        Water,
        Shore,
        Wader,
        Air
    }

    // How an animal crosses the world: one block column at a time, in any of the eight directions,
    // climbing at most one block and dropping at most three, and never squeezing diagonally between
    // two columns it could not step onto. The simulation and the client's display follow the same
    // rules, so an animal never passes through blocks: a wall two blocks high stops it, unless it flies.
    public static class AnimalMoves
    {
        public const int MaxClimb = 1;
        public const int MaxDrop = 3;

        private static readonly int[] DirectionX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] DirectionZ = { 0, 1, 1, 1, 0, -1, -1, -1 };

        // Straight toward the target first, then 45 degrees to either side, then 90: a sidestep is
        // what takes an animal around a block, since cutting diagonally past its corner is not allowed.
        private static readonly int[] Turns = { 0, 1, -1, 2, -2 };

        public static int DirectionCount => DirectionX.Length;

        public static int OffsetX(int direction) => DirectionX[direction];

        public static int OffsetZ(int direction) => DirectionZ[direction];

        // Whether an animal standing at height fromY can step to a neighbouring column whose ground is at toY.
        public static bool CanStep(int fromY, int toY) => toY - fromY <= MaxClimb && fromY - toY <= MaxDrop;

        private static bool CanStep(int fromY, int toY, bool flies) => flies || CanStep(fromY, toY);

        // Wading birds stand in water at most this deep.
        public const int MaxWadingDepth = 2;

        // The air block above a column's ground as land animals see it, or -1 if the top of the
        // column is water: they never walk into lakes and rivers, nor on their beds.
        public static int GroundOf(World world, int x, int z)
        {
            return TryGetGround(world, AnimalHabitat.Land, x, z, out int y) ? y : -1;
        }

        // Where an animal of this habitat stands in a column of this world, or false if it cannot.
        public static bool TryGetGround(World world, AnimalHabitat habitat, int x, int z, out int y)
        {
            int top = world.GetTopHeight(x, z);
            bool wet = top > 0 && world.IsWater(x, top - 1, z);
            int bed = wet ? world.GetSurfaceHeight(x, z) : top;
            return GroundFor(habitat, top, wet, bed, out y);
        }

        // Where an animal of this habitat stands in a column, from the column's top (the first air
        // block above everything, water included), whether water lies on top, and its bed (the first
        // block above its highest solid one: under water, the lowest water block):
        // - on land, the block above the ground; never on water;
        // - in water, the topmost water block (a fish swims just under the surface); never on land;
        // - on the shore, either: on land as land animals, and on water floating at its surface;
        // - wading, on land, or on the bed of water no deeper than MaxWadingDepth;
        // - in the air, on top of anything (ground or water): a bird lands where it likes.
        public static bool GroundFor(AnimalHabitat habitat, int top, bool wet, int bed, out int y)
        {
            y = -1;
            if (habitat == AnimalHabitat.Air)
            {
                y = top;
                return top > 0;
            }

            if (!wet)
            {
                if (habitat == AnimalHabitat.Water || top <= 0) return false;

                y = top;
                return true;
            }

            switch (habitat)
            {
                case AnimalHabitat.Water:
                    y = top - 1;
                    return true;
                case AnimalHabitat.Shore:
                    y = top;
                    return true;
                case AnimalHabitat.Wader:
                    if (top - bed > MaxWadingDepth) return false;

                    y = bed;
                    return true;
                default:
                    return false;
            }
        }

        // Distance in steps between two columns, diagonal steps included.
        public static int Steps(int x0, int z0, int x1, int z1) => Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));

        // The next column on the way from (x, z), at height y, toward (targetX, targetZ): straight if
        // possible, otherwise aside, as long as the step does not lead further away (an animal never
        // turns back). Returns false if the animal is already there or every such way is blocked. A
        // flying animal (flies) is not held back by heights: it goes over walls and cliffs.
        public static bool TryNextStep<TGround>(TGround ground, int x, int y, int z, int targetX, int targetZ, out int nextX, out int nextY, out int nextZ, bool flies = false)
            where TGround : IAnimalGround
        {
            nextX = x;
            nextY = y;
            nextZ = z;
            int distance = Steps(x, z, targetX, targetZ);
            if (distance == 0) return false;

            int straight = DirectionToward(targetX - x, targetZ - z);
            foreach (int turn in Turns)
            {
                int direction = (straight + turn + DirectionCount) % DirectionCount;
                int dx = DirectionX[direction];
                int dz = DirectionZ[direction];
                int candidateX = x + dx;
                int candidateZ = z + dz;
                if (Steps(candidateX, candidateZ, targetX, targetZ) > distance) continue;
                if (!CanEnter(ground, x, y, z, dx, dz, out int candidateY, flies)) continue;

                nextX = candidateX;
                nextY = candidateY;
                nextZ = candidateZ;
                return true;
            }

            return false;
        }

        // A step of (dx, dz) from (x, y, z): the destination must be free and within climbing or
        // dropping range (whatever the heights for a flying animal), and a diagonal step also needs
        // both columns it passes between to be.
        public static bool CanEnter<TGround>(TGround ground, int x, int y, int z, int dx, int dz, out int toY, bool flies = false)
            where TGround : IAnimalGround
        {
            if (!ground.TryGetGround(x + dx, z + dz, out toY) || !CanStep(y, toY, flies) || !ground.IsFree(x + dx, z + dz)) return false;
            if (dx == 0 || dz == 0) return true;

            return ground.TryGetGround(x + dx, z, out int sideY) && CanStep(y, sideY, flies)
                && ground.TryGetGround(x, z + dz, out int otherSideY) && CanStep(y, otherSideY, flies);
        }

        private static int DirectionToward(int dx, int dz)
        {
            int sx = Math.Sign(dx);
            int sz = Math.Sign(dz);
            for (int direction = 0; direction < DirectionCount; direction++)
            {
                if (DirectionX[direction] == sx && DirectionZ[direction] == sz) return direction;
            }

            return 0;
        }
    }
}
