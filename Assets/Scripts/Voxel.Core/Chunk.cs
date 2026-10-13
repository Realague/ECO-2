namespace Voxel.Core
{
    public sealed class Chunk
    {
        public const int SizeBits = 4;
        public const int Size = 1 << SizeBits;
        public const int SizeMask = Size - 1;
        public const int Volume = Size * Size * Size;

        private readonly ushort[] blocks = new ushort[Volume];

        public Chunk(ChunkCoord coord)
        {
            Coord = coord;
        }

        public ChunkCoord Coord { get; }

        // True as soon as a block differs from what the generator produced.
        public bool IsModified { get; internal set; }

        // Raw storage, in horizontal layers (x, then z, then y), for serialization.
        internal ushort[] RawBlocks => blocks;

        public ushort Get(int x, int y, int z) => blocks[Index(x, y, z)];

        // Raw write, reserved for generators: the game goes through World.SetBlock.
        public void Set(int x, int y, int z, ushort block) => blocks[Index(x, y, z)] = block;

        private static int Index(int x, int y, int z) => x | (z << SizeBits) | (y << (SizeBits * 2));
    }
}
