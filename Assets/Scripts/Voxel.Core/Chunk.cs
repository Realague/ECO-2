namespace Voxel.Core
{
    public sealed class Chunk
    {
        public const int SizeBits = 4;
        public const int Size = 1 << SizeBits;
        public const int SizeMask = Size - 1;
        public const int Volume = Size * Size * Size;

        private readonly ushort[] blocks = new ushort[Volume];

        // How many blocks are not air (by type): column scans skip empty chunks. Kept by Set; raw
        // writers (ChunkSerializer.Read) call RecountBlocks.
        private int filledCount;

        public Chunk(ChunkCoord coord)
        {
            Coord = coord;
        }

        public ChunkCoord Coord { get; }

        // True as soon as a block differs from what the generator produced.
        public bool IsModified { get; internal set; }

        // Raw storage, in horizontal layers (x, then z, then y), for serialization and column scans.
        internal ushort[] RawBlocks => blocks;

        // True when every block is air.
        public bool IsEmpty => filledCount == 0;

        public ushort Get(int x, int y, int z) => blocks[Index(x, y, z)];

        // Raw write, reserved for generators: the game goes through World.SetBlock.
        public void Set(int x, int y, int z, ushort block)
        {
            int index = Index(x, y, z);
            bool wasFilled = Blocks.TypeOf(blocks[index]) != Blocks.Air;
            bool isFilled = Blocks.TypeOf(block) != Blocks.Air;
            blocks[index] = block;
            if (wasFilled != isFilled) filledCount += isFilled ? 1 : -1;
        }

        // Counts the blocks again, after a raw write into RawBlocks.
        internal void RecountBlocks()
        {
            int count = 0;
            foreach (ushort block in blocks)
            {
                if (Blocks.TypeOf(block) != Blocks.Air) count++;
            }

            filledCount = count;
        }

        private static int Index(int x, int y, int z) => x | (z << SizeBits) | (y << (SizeBits * 2));
    }
}
