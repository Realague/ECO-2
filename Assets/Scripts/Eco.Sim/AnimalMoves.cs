using System;

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

    // How an animal crosses the world: one block column at a time, in any of the eight directions,
    // climbing at most one block and dropping at most three, and never squeezing diagonally between
    // two columns it could not step onto. The simulation and the client's display follow the same
    // rules, so an animal never passes through blocks: a wall two blocks high stops it.
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

        // Distance in steps between two columns, diagonal steps included.
        public static int Steps(int x0, int z0, int x1, int z1) => Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));

        // The next column on the way from (x, z), at height y, toward (targetX, targetZ): straight if
        // possible, otherwise aside, as long as the step does not lead further away (an animal never
        // turns back). Returns false if the animal is already there or every such way is blocked.
        public static bool TryNextStep<TGround>(TGround ground, int x, int y, int z, int targetX, int targetZ, out int nextX, out int nextY, out int nextZ)
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
                if (!CanEnter(ground, x, y, z, dx, dz, out int candidateY)) continue;

                nextX = candidateX;
                nextY = candidateY;
                nextZ = candidateZ;
                return true;
            }

            return false;
        }

        // A step of (dx, dz) from (x, y, z): the destination must be free and within climbing or
        // dropping range, and a diagonal step also needs both columns it passes between to be.
        public static bool CanEnter<TGround>(TGround ground, int x, int y, int z, int dx, int dz, out int toY)
            where TGround : IAnimalGround
        {
            if (!ground.TryGetGround(x + dx, z + dz, out toY) || !CanStep(y, toY) || !ground.IsFree(x + dx, z + dz)) return false;
            if (dx == 0 || dz == 0) return true;

            return ground.TryGetGround(x + dx, z, out int sideY) && CanStep(y, sideY)
                && ground.TryGetGround(x, z + dz, out int otherSideY) && CanStep(y, otherSideY);
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
