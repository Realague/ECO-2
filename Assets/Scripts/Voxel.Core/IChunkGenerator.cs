namespace Voxel.Core
{
    public interface IChunkGenerator
    {
        // To increment whenever the produced terrain changes: saves only store the modified
        // chunks, so they become invalid if the rest of the world is no longer the same.
        int Version { get; }

        // Fills the chunk. The result must only depend on its coordinates and the seed, never on
        // the order of calls or on the machine: a client thus regenerates the same world as the server.
        void Generate(Chunk chunk, int seed);
    }
}
