using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Voxel.Core
{
    // Fixed-size world: every chunk exists from construction.
    public sealed class World
    {
        private readonly Chunk[] chunks;
        private Geography geography;

        public World(int chunksX, int chunksY, int chunksZ)
        {
            if (chunksX <= 0) throw new ArgumentOutOfRangeException(nameof(chunksX));
            if (chunksY <= 0) throw new ArgumentOutOfRangeException(nameof(chunksY));
            if (chunksZ <= 0) throw new ArgumentOutOfRangeException(nameof(chunksZ));

            ChunksX = chunksX;
            ChunksY = chunksY;
            ChunksZ = chunksZ;

            chunks = new Chunk[chunksX * chunksY * chunksZ];
            for (int y = 0; y < chunksY; y++)
            {
                for (int z = 0; z < chunksZ; z++)
                {
                    for (int x = 0; x < chunksX; x++)
                    {
                        chunks[ChunkIndex(x, y, z)] = new Chunk(new ChunkCoord(x, y, z));
                    }
                }
            }
        }

        // Arguments: position, old block, new block.
        public event Action<BlockPos, ushort, ushort> BlockChanged;

        public int ChunksX { get; }
        public int ChunksY { get; }
        public int ChunksZ { get; }

        // Set by Generate: together with the modified chunks, they are enough to rebuild the world.
        public int Seed { get; private set; }
        public int GeneratorVersion { get; private set; }

        // Fingerprint of the terrain as generated, before any edit. A client compares it to the
        // server's to make sure it generated the same world.
        public ulong TerrainFingerprint { get; private set; }

        public int SizeX => ChunksX * Chunk.Size;
        public int SizeY => ChunksY * Chunk.Size;
        public int SizeZ => ChunksZ * Chunk.Size;

        public IReadOnlyList<Chunk> Chunks => chunks;

        // The climate laid out by generation, worked out from the seed and size the first time it is
        // asked for. Changing blocks never changes it.
        public Geography Geography => geography ?? (geography = Geography.Create(Seed, SizeX, SizeZ));

        public void Generate(IChunkGenerator generator, int seed)
        {
            Seed = seed;
            GeneratorVersion = generator.Version;
            geography = null;
            generator.Prepare(seed, SizeX, SizeZ);

            if (generator is IParallelChunkGenerator)
            {
                Parallel.For(0, chunks.Length, i => generator.Generate(chunks[i], seed));
            }
            else
            {
                foreach (Chunk chunk in chunks)
                {
                    generator.Generate(chunk, seed);
                }
            }

            TerrainFingerprint = ComputeFingerprint();
        }

        // Summarises the relief by the height and surface block of a regular sample of columns.
        private ulong ComputeFingerprint()
        {
            const int samplesPerAxis = 32;
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offsetBasis;
            int stepX = Math.Max(1, SizeX / samplesPerAxis);
            int stepZ = Math.Max(1, SizeZ / samplesPerAxis);
            for (int z = 0; z < SizeZ; z += stepZ)
            {
                for (int x = 0; x < SizeX; x += stepX)
                {
                    int height = GetSurfaceHeight(x, z);
                    hash = unchecked((hash ^ (uint)height) * prime);
                    hash = unchecked((hash ^ GetBlock(x, height - 1, z)) * prime);
                }
            }

            return hash;
        }

        public bool InBounds(int x, int y, int z) =>
            (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

        public Chunk GetChunk(ChunkCoord coord)
        {
            bool inside = (uint)coord.X < (uint)ChunksX && (uint)coord.Y < (uint)ChunksY && (uint)coord.Z < (uint)ChunksZ;
            return inside ? chunks[ChunkIndex(coord.X, coord.Y, coord.Z)] : null;
        }

        public ushort GetBlock(BlockPos pos) => GetBlock(pos.X, pos.Y, pos.Z);

        public ushort GetBlock(int x, int y, int z)
        {
            if (!InBounds(x, y, z)) return Blocks.Air;
            return ChunkAt(x, y, z).Get(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
        }

        // Outside the world, the sides and the bottom count as solid: they keep players in
        // and avoid meshing invisible faces. Above the world, it is air.
        public bool IsSolid(int x, int y, int z)
        {
            if (y >= SizeY) return false;
            if (!InBounds(x, y, z)) return true;
            return Blocks.IsSolid(GetBlock(x, y, z));
        }

        // Whether the block hides what is behind it, for drawing; outside the world as IsSolid.
        public bool IsOpaque(int x, int y, int z)
        {
            if (y >= SizeY) return false;
            if (!InBounds(x, y, z)) return true;
            return Blocks.IsOpaque(GetBlock(x, y, z));
        }

        public bool IsWater(int x, int y, int z) => Blocks.IsWater(GetBlock(x, y, z));

        public bool SetBlock(BlockPos pos, ushort block) => SetBlock(pos.X, pos.Y, pos.Z, block);

        // Only entry point to change a block in game. Returns false if nothing changed.
        public bool SetBlock(int x, int y, int z, ushort block)
        {
            if (!InBounds(x, y, z)) return false;

            Chunk chunk = ChunkAt(x, y, z);
            int localX = x & Chunk.SizeMask;
            int localY = y & Chunk.SizeMask;
            int localZ = z & Chunk.SizeMask;

            ushort previous = chunk.Get(localX, localY, localZ);
            if (previous == block) return false;

            chunk.Set(localX, localY, localZ, block);
            chunk.IsModified = true;
            BlockChanged?.Invoke(new BlockPos(x, y, z), previous, block);
            return true;
        }

        // Height of the first block above the topmost solid block of the column: air, or water when
        // the column is under water (the ground there is a lake's bed).
        public int GetSurfaceHeight(int x, int z)
        {
            if ((uint)x >= (uint)SizeX || (uint)z >= (uint)SizeZ) return 0;

            // Chunk by chunk from the top, skipping empty ones, reading their blocks directly: the
            // simulation and the maps look columns up by the hundred thousand.
            int column = (x & Chunk.SizeMask) | ((z & Chunk.SizeMask) << Chunk.SizeBits);
            for (int chunkY = ChunksY - 1; chunkY >= 0; chunkY--)
            {
                Chunk chunk = chunks[ChunkIndex(x >> Chunk.SizeBits, chunkY, z >> Chunk.SizeBits)];
                if (chunk.IsEmpty) continue;

                ushort[] blocks = chunk.RawBlocks;
                for (int localY = Chunk.SizeMask; localY >= 0; localY--)
                {
                    if (Blocks.IsSolid(blocks[column | (localY << (2 * Chunk.SizeBits))])) return chunkY * Chunk.Size + localY + 1;
                }
            }

            return 0;
        }

        // Height of the first air block above the topmost block of the column, water included (torches
        // left out: they stand in the air): what one sees from above.
        public int GetTopHeight(int x, int z)
        {
            if ((uint)x >= (uint)SizeX || (uint)z >= (uint)SizeZ) return 0;

            int column = (x & Chunk.SizeMask) | ((z & Chunk.SizeMask) << Chunk.SizeBits);
            for (int chunkY = ChunksY - 1; chunkY >= 0; chunkY--)
            {
                Chunk chunk = chunks[ChunkIndex(x >> Chunk.SizeBits, chunkY, z >> Chunk.SizeBits)];
                if (chunk.IsEmpty) continue;

                ushort[] blocks = chunk.RawBlocks;
                for (int localY = Chunk.SizeMask; localY >= 0; localY--)
                {
                    if (Blocks.FillsColumn(blocks[column | (localY << (2 * Chunk.SizeBits))])) return chunkY * Chunk.Size + localY + 1;
                }
            }

            return 0;
        }

        private Chunk ChunkAt(int x, int y, int z) =>
            chunks[ChunkIndex(x >> Chunk.SizeBits, y >> Chunk.SizeBits, z >> Chunk.SizeBits)];

        private int ChunkIndex(int x, int y, int z) => x + ChunksX * (z + ChunksZ * y);
    }
}
