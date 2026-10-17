namespace Voxel.Core.Tests
{
    // Flat stone ground: solid below GroundHeight, empty above.
    internal sealed class FlatGenerator : IChunkGenerator
    {
        public const int GroundHeight = 8;

        public int Version { get; set; } = 1;

        public static World CreateWorld()
        {
            var world = new World(1, 1, 1);
            world.Generate(new FlatGenerator(), 0);
            return world;
        }

        public void Prepare(int seed, int sizeX, int sizeZ)
        {
        }

        public void Generate(Chunk chunk, int seed)
        {
            int originY = chunk.Coord.Y * Chunk.Size;
            for (int y = 0; y < Chunk.Size; y++)
            {
                ushort block = originY + y < GroundHeight ? Blocks.Stone : Blocks.Air;
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        chunk.Set(x, y, z, block);
                    }
                }
            }
        }
    }
}
