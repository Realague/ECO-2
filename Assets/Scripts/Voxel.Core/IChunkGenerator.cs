namespace Voxel.Core
{
    public interface IChunkGenerator
    {
        // À incrémenter dès que le terrain produit change : les sauvegardes ne stockent que les
        // chunks modifiés, elles deviennent donc invalides si le reste du monde n'est plus le même.
        int Version { get; }

        // Remplit le chunk à partir de ses coordonnées et de la graine, sans autre état.
        void Generate(Chunk chunk, int seed);
    }
}
