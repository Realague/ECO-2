namespace Voxel.Core
{
    public sealed class Chunk
    {
        public const int SizeBits = 4;
        public const int Size = 1 << SizeBits;
        public const int SizeMask = Size - 1;
        public const int Volume = Size * Size * Size;

        private readonly byte[] blocks = new byte[Volume];

        public Chunk(ChunkCoord coord)
        {
            Coord = coord;
        }

        public ChunkCoord Coord { get; }

        // Vrai dès qu'un bloc diffère de ce que le générateur a produit.
        public bool IsModified { get; internal set; }

        public byte Get(int x, int y, int z) => blocks[Index(x, y, z)];

        // Écriture brute, réservée aux générateurs : le jeu passe par World.SetBlock.
        public void Set(int x, int y, int z, byte block) => blocks[Index(x, y, z)] = block;

        private static int Index(int x, int y, int z) => x | (z << SizeBits) | (y << (SizeBits * 2));
    }
}
