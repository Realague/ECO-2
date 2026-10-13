using System.Collections.Generic;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // Prairie plate : pierre, une couche de terre, une couche d'herbe, puis de l'air.
    internal sealed class MeadowGenerator : IChunkGenerator
    {
        // Hauteur de la première case d'air au-dessus de l'herbe.
        public const int Surface = 8;

        public int Version => 1;

        public static World CreateWorld(int chunksPerSide, int seed = 0)
        {
            var world = new World(chunksPerSide, 1, chunksPerSide);
            world.Generate(new MeadowGenerator(), seed);
            return world;
        }

        public void Generate(Chunk chunk, int seed)
        {
            for (int y = 0; y < Chunk.Size; y++)
            {
                ushort block = y >= Surface ? Blocks.Air : y == Surface - 1 ? Blocks.Grass : y == Surface - 2 ? Blocks.Dirt : Blocks.Stone;
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

    internal static class TestSpecies
    {
        public static Species Tree() => new Species
        {
            Name = "Arbre",
            MaturitySteps = 3 * SimClock.StepsPerDay,
            LifespanSteps = 30 * SimClock.StepsPerDay,
            SeedChance = 0.01f,
            SeedRange = 6,
            Spacing = 2,
            MinFertility = 0.2f,
            FertilityUse = 0.0015f,
            Height = 5.5f,
            Width = 1f
        };

        public static Species Bush() => new Species
        {
            Name = "Buisson",
            MaturitySteps = SimClock.StepsPerDay,
            LifespanSteps = 10 * SimClock.StepsPerDay,
            SeedChance = 0.02f,
            SeedRange = 3,
            Spacing = 1,
            MinFertility = 0.5f,
            FertilityUse = 0.0008f,
            Height = 0.9f,
            Width = 0.9f
        };

        public static IReadOnlyList<Species> TreeOnly() => new[] { Tree() };

        public static IReadOnlyList<Species> TreeAndBush() => new[] { Tree(), Bush() };
    }
}
