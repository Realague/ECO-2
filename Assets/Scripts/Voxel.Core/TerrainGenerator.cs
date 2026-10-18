using System;

namespace Voxel.Core
{
    // Procedural terrain: plains, hills and rocky massifs (ReliefHeight), with lakes, rivers and a
    // sea along the west edge, the rocks under them and the caves in those rocks, laid out for the
    // whole world by Landscape; ores are scattered here, block by block. The terrain only depends
    // on the coordinates, the seed and the world's size, and all the math is in integers (see
    // IntNoise): the server and every client therefore get exactly the same world. The relief fits
    // within 64 blocks of height.
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

        private const int DirtDepth = 3;
        private const int MarbleThickness = 2;
        private const uint BedrockSeedMask = 0x2545F491u;
        private const uint OreSeedMask = 0x7FEB352Du;
        private const uint OreBlockSeedMask = 0x846CA68Bu;

        private Landscape landscape;

        public int Version => 6;

        // Height of the topmost block of the relief, before Landscape digs the lakes, the sea and
        // the rivers: plains at BaseHeight, hills, massifs.
        public static int ReliefHeight(int x, int z, int seed)
        {
            uint noiseSeed = unchecked((uint)seed);

            long massif = Stretch(IntNoise.TurnedFractal(x, z, MassifCellShift, 1, noiseSeed ^ MassifSeedMask), MassifFloor, MassifGainTenths);
            long hills = Stretch(IntNoise.TurnedFractal(x, z, HillsCellShift, HillsOctaves, noiseSeed), HillsFloor, HillsGainTenths);
            long relief = (long)PlainsRelief * IntNoise.One + massif * MountainRelief;
            return BaseHeight + (int)((hills * relief) >> 32);
        }

        private static long Stretch(long value, long floor, long gainTenths)
        {
            long stretched = (value - floor) * gainTenths / 10;
            return Math.Min(Math.Max(stretched, 0), IntNoise.One - 1);
        }

        public void Prepare(int seed, int sizeX, int sizeZ)
        {
            landscape = Landscape.For(seed, sizeX, sizeZ);
        }

        public void Generate(Chunk chunk, int seed)
        {
            if (landscape == null) throw new InvalidOperationException("Prepare must be called before Generate.");

            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;
            uint noiseSeed = unchecked((uint)seed);

            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    int worldX = originX + x;
                    int worldZ = originZ + z;
                    var column = new ColumnPlan
                    {
                        X = worldX,
                        Z = worldZ,
                        Ground = landscape.Ground(worldX, worldZ),
                        Water = landscape.WaterTop(worldX, worldZ),
                        Rock = landscape.IsRock(worldX, worldZ),
                        Ice = landscape.IsIce(worldX, worldZ),
                        Geology = landscape.Column(worldX, worldZ),
                        Seed = noiseSeed
                    };

                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        chunk.Set(x, y, z, BlockAt(originY + y, column));
                    }
                }
            }

            landscape.Caves.Carve(chunk);
        }

        private struct ColumnPlan
        {
            public int X;
            public int Z;
            public int Ground;
            public int Water;
            public bool Rock;
            public bool Ice;
            public Landscape.ColumnGeology Geology;
            public uint Seed;
        }

        // From the top down: air, water (ice on top where it is cold), the top block and the three
        // under it (grass on dirt, sand, snow; under water the bed), then rock: the cover rock, its
        // marble, the granite basement and the bedrock, with ores in it. Bare rock has rock up to
        // its top, or snow on it.
        private static ushort BlockAt(int y, in ColumnPlan column)
        {
            int ground = column.Ground;
            if (y > ground)
            {
                if (y > column.Water) return Blocks.Air;
                return column.Ice && y == column.Water ? Blocks.Ice : Blocks.Water;
            }

            bool underWater = column.Water > ground;
            if (column.Rock && !underWater)
            {
                if (y == ground && column.Geology.Top == Blocks.Snow) return Blocks.Snow;
                return RockAt(y, column);
            }

            if (y == ground) return column.Geology.Top;
            if (y >= ground - DirtDepth) return column.Geology.Under;
            return RockAt(y, column);
        }

        // The bottom of the world is bedrock, ragged on its top two blocks.
        private static ushort RockAt(int y, in ColumnPlan column)
        {
            uint hash = Hash(column.X, y, column.Z, column.Seed ^ BedrockSeedMask);
            if (y == 0 || (y == 1 && (hash & 1) == 0) || (y == 2 && (hash & 3) == 0)) return Blocks.Bedrock;

            Landscape.ColumnGeology geology = column.Geology;
            ushort host;
            if (y < geology.CoverBottom) host = Blocks.Granite;
            else if (geology.Marble && y < geology.CoverBottom + MarbleThickness) return Blocks.Marble;
            else host = geology.Cover;

            return OreAt(column.X, y, column.Z, column.Ground - y, host, geology.Cold, column.Seed);
        }

        // Ores lie in clumps: the rock is cut into cells of 4 × 4 × 4 blocks, and a cell holds ore
        // with a chance that depends on its host rock and depth; then three blocks in four of the
        // cell are ore. Sedimentary rocks hold coal, limestone flint near the surface, sandstone
        // rock salt; granite copper, tin in cold regions and gold deep down; basalt iron; and iron
        // is found a little everywhere. Chances are in 1/1024 per cell.
        private static ushort OreAt(int x, int y, int z, int depth, ushort host, bool cold, uint seed)
        {
            uint cell = Hash(x >> 2, y >> 2, z >> 2, seed ^ OreSeedMask);
            if ((Hash(x, y, z, seed ^ OreBlockSeedMask) & 3) == 0) return host;

            int roll = (int)(cell & 1023);
            int kindRoll = (int)((cell >> 10) & 1023);
            switch (host)
            {
                case Blocks.Limestone:
                case Blocks.Sandstone:
                case Blocks.Slate:
                    if (host == Blocks.Limestone && depth < 10 && roll < 20) return Blocks.Flint;
                    if (host == Blocks.Sandstone && depth >= 6 && roll < 18) return Blocks.RockSalt;
                    if (depth >= 5 && roll >= 20 && roll < 50) return Blocks.CoalOre;
                    if (roll >= 50 && roll < 60) return Blocks.IronOre;
                    return host;
                case Blocks.Granite:
                    if (depth >= 25 && roll < 5) return Blocks.GoldOre;
                    if (cold && roll >= 5 && roll < 17) return Blocks.TinOre;
                    if (depth < 40 && roll >= 17 && roll < 37) return Blocks.CopperOre;
                    if (roll >= 37 && roll < 47 + (kindRoll & 3)) return Blocks.IronOre;
                    return host;
                case Blocks.Basalt:
                    return roll < 40 ? Blocks.IronOre : host;
                default:
                    return host;
            }
        }

        private static uint Hash(int x, int y, int z, uint seed)
        {
            unchecked
            {
                uint hash = seed ^ 0x9E3779B9u;
                hash = (hash ^ (uint)x) * 0x85EBCA6Bu;
                hash = (hash ^ (hash >> 13) ^ (uint)y) * 0xC2B2AE35u;
                hash = (hash ^ (hash >> 16) ^ (uint)z) * 0x27D4EB2Fu;
                hash ^= hash >> 15;
                return hash;
            }
        }
    }
}
