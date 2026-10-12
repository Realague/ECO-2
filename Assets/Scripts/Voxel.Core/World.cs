using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // Monde de taille fixe : tous les chunks existent dès la construction.
    public sealed class World
    {
        private readonly Chunk[] chunks;

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

        // Arguments : position, ancien bloc, nouveau bloc.
        public event Action<BlockPos, byte, byte> BlockChanged;

        public int ChunksX { get; }
        public int ChunksY { get; }
        public int ChunksZ { get; }

        public int SizeX => ChunksX * Chunk.Size;
        public int SizeY => ChunksY * Chunk.Size;
        public int SizeZ => ChunksZ * Chunk.Size;

        public IReadOnlyList<Chunk> Chunks => chunks;

        public void Generate(IChunkGenerator generator, int seed)
        {
            foreach (Chunk chunk in chunks)
            {
                generator.Generate(chunk, seed);
            }
        }

        public bool InBounds(int x, int y, int z) =>
            (uint)x < (uint)SizeX && (uint)y < (uint)SizeY && (uint)z < (uint)SizeZ;

        public Chunk GetChunk(ChunkCoord coord)
        {
            bool inside = (uint)coord.X < (uint)ChunksX && (uint)coord.Y < (uint)ChunksY && (uint)coord.Z < (uint)ChunksZ;
            return inside ? chunks[ChunkIndex(coord.X, coord.Y, coord.Z)] : null;
        }

        public byte GetBlock(BlockPos pos) => GetBlock(pos.X, pos.Y, pos.Z);

        public byte GetBlock(int x, int y, int z)
        {
            if (!InBounds(x, y, z)) return Blocks.Air;
            return ChunkAt(x, y, z).Get(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
        }

        // Hors du monde, les côtés et le dessous comptent comme pleins : ils retiennent les joueurs
        // et évitent de mailler des faces invisibles. Au-dessus du monde, c'est de l'air.
        public bool IsSolid(int x, int y, int z)
        {
            if (y >= SizeY) return false;
            if (!InBounds(x, y, z)) return true;
            return Blocks.IsSolid(GetBlock(x, y, z));
        }

        public bool SetBlock(BlockPos pos, byte block) => SetBlock(pos.X, pos.Y, pos.Z, block);

        // Seul point d'entrée pour modifier un bloc en jeu. Renvoie faux si rien n'a changé.
        public bool SetBlock(int x, int y, int z, byte block)
        {
            if (!InBounds(x, y, z)) return false;

            Chunk chunk = ChunkAt(x, y, z);
            int localX = x & Chunk.SizeMask;
            int localY = y & Chunk.SizeMask;
            int localZ = z & Chunk.SizeMask;

            byte previous = chunk.Get(localX, localY, localZ);
            if (previous == block) return false;

            chunk.Set(localX, localY, localZ, block);
            chunk.IsModified = true;
            BlockChanged?.Invoke(new BlockPos(x, y, z), previous, block);
            return true;
        }

        // Hauteur du premier bloc d'air au-dessus du plus haut bloc plein de la colonne.
        public int GetSurfaceHeight(int x, int z)
        {
            for (int y = SizeY - 1; y >= 0; y--)
            {
                if (Blocks.IsSolid(GetBlock(x, y, z))) return y + 1;
            }

            return 0;
        }

        private Chunk ChunkAt(int x, int y, int z) =>
            chunks[ChunkIndex(x >> Chunk.SizeBits, y >> Chunk.SizeBits, z >> Chunk.SizeBits)];

        private int ChunkIndex(int x, int y, int z) => x + ChunksX * (z + ChunksZ * y);
    }
}
