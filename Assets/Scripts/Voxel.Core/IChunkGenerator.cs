namespace Voxel.Core
{
    public interface IChunkGenerator
    {
        // À incrémenter dès que le terrain produit change : les sauvegardes ne stockent que les
        // chunks modifiés, elles deviennent donc invalides si le reste du monde n'est plus le même.
        int Version { get; }

        // Remplit le chunk. Le résultat ne doit dépendre que de ses coordonnées et de la graine, jamais
        // de l'ordre des appels ni de la machine : un client régénère ainsi le même monde que le serveur.
        void Generate(Chunk chunk, int seed);
    }
}
