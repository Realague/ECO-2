namespace Voxel.Core
{
    // Two-dimensional value noise, computed with integers only. The resulting terrain is thus
    // bit-identical on every machine, which floats do not guarantee:
    // this is what lets a client generate the same world as the server by itself.
    // Values are fixed point: One stands for 1.
    public static class IntNoise
    {
        public const int One = 1 << 16;

        // Smoothed noise on a grid of cells 2^cellShift blocks wide. Result in [0, One).
        public static int Sample(int x, int z, int cellShift, uint seed)
        {
            int mask = (1 << cellShift) - 1;
            int cellX = x >> cellShift;
            int cellZ = z >> cellShift;
            long tx = Smooth(((long)(x & mask) << 16) >> cellShift);
            long tz = Smooth(((long)(z & mask) << 16) >> cellShift);

            long a = Lattice(cellX, cellZ, seed);
            long b = Lattice(cellX + 1, cellZ, seed);
            long c = Lattice(cellX, cellZ + 1, seed);
            long d = Lattice(cellX + 1, cellZ + 1, seed);

            long near = a + (((b - a) * tx) >> 16);
            long far = c + (((d - c) * tx) >> 16);
            return (int)(near + (((far - near) * tz) >> 16));
        }

        // Sum of several layers of noise: each has cells half the size of the previous one's
        // and weighs half as much. Result in [0, One).
        public static int Fractal(int x, int z, int firstCellShift, int octaves, uint seed)
        {
            long sum = 0;
            long total = 0;
            long weight = One;
            for (int octave = 0; octave < octaves; octave++)
            {
                uint octaveSeed = unchecked(seed + (uint)octave * 0x9E3779B9u);
                sum += (Sample(x, z, firstCellShift - octave, octaveSeed) * weight) >> 16;
                total += weight;
                weight >>= 1;
            }

            return (int)((sum << 16) / total);
        }

        // Pseudo-random value of a grid point, in [0, One).
        private static long Lattice(int x, int z, uint seed)
        {
            unchecked
            {
                uint hash = seed ^ 0x9E3779B9u;
                hash = (hash ^ (uint)x) * 0x85EBCA6Bu;
                hash = (hash ^ (hash >> 13) ^ (uint)z) * 0xC2B2AE35u;
                hash ^= hash >> 16;
                hash *= 0x27D4EB2Fu;
                hash ^= hash >> 15;
                return hash >> 16;
            }
        }

        // S-curve (3t² − 2t³): it smooths out the corners at cell boundaries.
        private static long Smooth(long t) => (((t * t) >> 16) * (3L * One - 2L * t)) >> 16;
    }
}
