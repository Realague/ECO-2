using System;

namespace Voxel.Core
{
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public ChunkCoord(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static ChunkCoord FromBlock(BlockPos block) =>
            new ChunkCoord(block.X >> Chunk.SizeBits, block.Y >> Chunk.SizeBits, block.Z >> Chunk.SizeBits);

        public static bool operator ==(ChunkCoord a, ChunkCoord b) => a.Equals(b);
        public static bool operator !=(ChunkCoord a, ChunkCoord b) => !a.Equals(b);

        public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is ChunkCoord other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
