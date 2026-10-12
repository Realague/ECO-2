namespace Voxel.Core.Tests
{
    // Sol de pierre plat : plein en dessous de GroundHeight, vide au-dessus.
    internal sealed class FlatGenerator : IChunkGenerator
    {
        public const int GroundHeight = 8;

        public static World CreateWorld()
        {
            var world = new World(1, 1, 1);
            world.Generate(new FlatGenerator(), 0);
            return world;
        }

        public void Generate(Chunk chunk, int seed)
        {
            int originY = chunk.Coord.Y * Chunk.Size;
            for (int y = 0; y < Chunk.Size; y++)
            {
                byte block = originY + y < GroundHeight ? Blocks.Stone : Blocks.Air;
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
