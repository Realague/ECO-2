namespace Voxel.Core
{
    public interface IChunkGenerator
    {
        // To increment whenever the produced terrain changes: saves only store the modified
        // chunks, so they become invalid if the rest of the world is no longer the same.
        int Version { get; }

        // Called once by World.Generate before any chunk, with the world's size in blocks: what
        // depends on the whole world (rivers, lakes) is worked out here.
        void Prepare(int seed, int sizeX, int sizeZ);

        // Fills the chunk. The result must only depend on its coordinates, the seed and the world's
        // size, never on the order of calls or on the machine: a client thus regenerates the same
        // world as the server.
        void Generate(Chunk chunk, int seed);
    }

    // A generator whose Generate only reads what Prepare worked out and only writes its own chunk:
    // World.Generate then fills the chunks on every core at once.
    public interface IParallelChunkGenerator : IChunkGenerator
    {
    }
}
