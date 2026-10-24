using System;

namespace Voxel.Core
{
    public readonly struct BlockPos : IEquatable<BlockPos>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public BlockPos(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static BlockPos operator +(BlockPos a, BlockPos b) => new BlockPos(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static BlockPos operator -(BlockPos a, BlockPos b) => new BlockPos(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static bool operator ==(BlockPos a, BlockPos b) => a.Equals(b);
        public static bool operator !=(BlockPos a, BlockPos b) => !a.Equals(b);

        public bool Equals(BlockPos other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is BlockPos other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}
