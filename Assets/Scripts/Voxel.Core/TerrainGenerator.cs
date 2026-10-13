using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // Procedural relief: plains, hills and rocky massifs. The terrain only depends on the
    // coordinates and the seed, and all the math is in integers (see IntNoise): the server and
    // every client therefore get exactly the same world. The relief fits within 64 blocks of height.
    public sealed class TerrainGenerator : IChunkGenerator
    {
        private const int BaseHeight = 20;

        // Height range of the plains, and height added at the heart of the massifs, in blocks.
        private const int PlainsRelief = 7;
        private const int MountainRelief = 33;

        // Raw noise clusters around its mean: it is stretched to get real plains
        // and real peaks. Below the floor, the value drops to zero; the gain is in tenths.
        // With these settings, more than half of the terrain is plain and about 5% is high rock.
        private const int MassifFloor = IntNoise.One * 35 / 100;
        private const int MassifGainTenths = 25;
        private const int HillsFloor = IntNoise.One * 20 / 100;
        private const int HillsGainTenths = 17;

        // Massifs span about 128 blocks, the largest hills about 64.
        private const int MassifCellShift = 7;
        private const int HillsCellShift = 6;
        private const int HillsOctaves = 4;
        private const uint MassifSeedMask = 0xA5A5A5A5u;

        // Rock is bare at altitude and on steep slopes; elsewhere, dirt under grass.
        private const int RockAltitude = 46;
        private const int RockSlope = 3;
        private const int DirtDepth = 3;

        private const int Patch = Chunk.Size + 2;

        // Relief already computed, per column of chunks: stacked chunks share the same one.
        private readonly Dictionary<long, ColumnData> cache = new Dictionary<long, ColumnData>();
        private int cachedSeed;

        public int Version => 2;

        // Height of the topmost block of the column.
        public static int SurfaceHeight(int x, int z, int seed)
        {
            uint noiseSeed = unchecked((uint)seed);

            long massif = Stretch(IntNoise.Sample(x, z, MassifCellShift, noiseSeed ^ MassifSeedMask), MassifFloor, MassifGainTenths);
            long hills = Stretch(IntNoise.Fractal(x, z, HillsCellShift, HillsOctaves, noiseSeed), HillsFloor, HillsGainTenths);
            long relief = (long)PlainsRelief * IntNoise.One + massif * MountainRelief;
            return BaseHeight + (int)((hills * relief) >> 32);
        }

        private static long Stretch(long value, long floor, long gainTenths)
        {
            long stretched = (value - floor) * gainTenths / 10;
            return Math.Min(Math.Max(stretched, 0), IntNoise.One - 1);
        }

        public void Generate(Chunk chunk, int seed)
        {
            ColumnData columns = ColumnsOf(chunk.Coord.X, chunk.Coord.Z, seed);
            int originY = chunk.Coord.Y * Chunk.Size;

            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    int column = x + Chunk.Size * z;
                    int surface = columns.Surface[column];
                    bool rock = columns.Rock[column];
                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        chunk.Set(x, y, z, BlockAt(originY + y, surface, rock));
                    }
                }
            }
        }

        private static ushort BlockAt(int y, int surface, bool rock)
        {
            if (y > surface) return Blocks.Air;
            if (rock) return Blocks.Stone;
            if (y == surface) return Blocks.Grass;
            if (y >= surface - DirtDepth) return Blocks.Dirt;
            return Blocks.Stone;
        }

        private ColumnData ColumnsOf(int chunkX, int chunkZ, int seed)
        {
            if (seed != cachedSeed)
            {
                cache.Clear();
                cachedSeed = seed;
            }

            long key = ((long)chunkX << 32) | (uint)chunkZ;
            if (cache.TryGetValue(key, out ColumnData columns)) return columns;

            columns = ComputeColumns(chunkX * Chunk.Size, chunkZ * Chunk.Size, seed);
            cache.Add(key, columns);
            return columns;
        }

        private static ColumnData ComputeColumns(int originX, int originZ, int seed)
        {
            // Heights of the chunk and of one column around it, to know the slope at its edges.
            var heights = new int[Patch * Patch];
            for (int z = 0; z < Patch; z++)
            {
                for (int x = 0; x < Patch; x++)
                {
                    heights[x + Patch * z] = SurfaceHeight(originX + x - 1, originZ + z - 1, seed);
                }
            }

            var columns = new ColumnData();
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    int centre = x + 1 + Patch * (z + 1);
                    int height = heights[centre];
                    int slope = Math.Max(
                        Math.Max(Math.Abs(heights[centre - 1] - height), Math.Abs(heights[centre + 1] - height)),
                        Math.Max(Math.Abs(heights[centre - Patch] - height), Math.Abs(heights[centre + Patch] - height)));

                    int column = x + Chunk.Size * z;
                    columns.Surface[column] = height;
                    columns.Rock[column] = height >= RockAltitude || slope >= RockSlope;
                }
            }

            return columns;
        }

        private sealed class ColumnData
        {
            public readonly int[] Surface = new int[Chunk.Size * Chunk.Size];
            public readonly bool[] Rock = new bool[Chunk.Size * Chunk.Size];
        }
    }
}
