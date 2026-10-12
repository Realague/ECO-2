namespace Voxel.Core
{
    public interface IChunkGenerator
    {
        // Remplit le chunk à partir de ses coordonnées et de la graine, sans autre état.
        void Generate(Chunk chunk, int seed);
    }
}
