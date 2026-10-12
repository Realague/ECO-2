using System;

namespace Voxel.Core
{
    // Générateur provisoire : collines douces, en attendant la vraie génération procédurale.
    public sealed class ProvisionalHillsGenerator : IChunkGenerator
    {
        private const int BaseHeight = 24;
        private const double WaveAmplitude = 2.0;
        private const int DirtDepth = 3;

        public int Version => 1;

        public void Generate(Chunk chunk, int seed)
        {
            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;

            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    int surface = SurfaceHeight(originX + x, originZ + z, seed);
                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        chunk.Set(x, y, z, BlockAt(originY + y, surface));
                    }
                }
            }
        }

        private static int SurfaceHeight(int x, int z, int seed)
        {
            double phase = seed % 1000 * 0.1;
            double waves = Math.Sin(x * 0.08 + phase) + Math.Cos(z * 0.11 - phase);
            return BaseHeight + (int)Math.Round(waves * WaveAmplitude);
        }

        private static ushort BlockAt(int y, int surface)
        {
            if (y > surface) return Blocks.Air;
            if (y == surface) return Blocks.Grass;
            if (y >= surface - DirtDepth) return Blocks.Dirt;
            return Blocks.Stone;
        }
    }
}
