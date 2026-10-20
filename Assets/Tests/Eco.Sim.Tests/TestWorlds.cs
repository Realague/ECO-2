using System.Collections.Generic;
using System.IO;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // Flat meadow: stone, one layer of dirt, one layer of grass, then air.
    internal sealed class MeadowGenerator : IChunkGenerator
    {
        // Height of the first air cell above the grass.
        public const int Surface = 8;

        public int Version => 1;

        public static World CreateWorld(int chunksPerSide, int seed = 0)
        {
            var world = new World(chunksPerSide, 1, chunksPerSide);
            world.Generate(new MeadowGenerator(), seed);
            return world;
        }

        public void Prepare(int seed, int sizeX, int sizeZ)
        {
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
            Name = "Tree",
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
            Name = "Bush",
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

    internal static class OldSaves
    {
        // A save in the current format, turned into format 5, written before crops ripened and
        // before the soil had nutrients: each plant without its ripeness, each cell with a single
        // fertility at its capacity.
        public static byte[] WithoutFruit(byte[] data, World world) => Convert(data, world, 5, 1f);

        // The same in format 6: ripeness kept, each cell a fertility at this share of its capacity.
        public static byte[] WithoutNutrients(byte[] data, World world, float share) => Convert(data, world, 6, share);

        private static byte[] Convert(byte[] data, World world, int format, float share)
        {
            var input = new BinaryReader(new MemoryStream(data));
            var output = new MemoryStream();
            var writer = new BinaryWriter(output);

            writer.Write(input.ReadUInt32());
            input.ReadInt32();
            writer.Write(format);
            int speciesCount = input.ReadInt32();
            writer.Write(speciesCount);
            for (int i = 0; i < speciesCount; i++) writer.Write(input.ReadString());
            for (int i = 0; i < 4; i++) writer.Write(input.ReadInt32());
            writer.Write(input.ReadInt64());
            writer.Write(input.ReadUInt64());
            writer.Write(input.ReadInt32());

            // Format 7 keeps three nutrient levels per cell; before, a fertility and its capacity.
            int cells = (world.SizeX + Ecosystem.CellSize - 1) / Ecosystem.CellSize * ((world.SizeZ + Ecosystem.CellSize - 1) / Ecosystem.CellSize);
            for (int i = 0; i < cells; i++)
            {
                for (int n = 0; n < Ecosystem.NutrientCount; n++) input.ReadSingle();
                writer.Write(share);
                writer.Write(1f);
            }

            int plantCount = input.ReadInt32();
            writer.Write(plantCount);
            for (int i = 0; i < plantCount; i++)
            {
                for (int field = 0; field < 6; field++) writer.Write(input.ReadInt32());
                writer.Write(input.ReadSingle());
                float fruit = input.ReadSingle();
                if (format >= 6) writer.Write(fruit);
            }

            byte[] rest = input.ReadBytes(data.Length);
            writer.Write(rest);
            writer.Flush();
            return output.ToArray();
        }
    }
}
