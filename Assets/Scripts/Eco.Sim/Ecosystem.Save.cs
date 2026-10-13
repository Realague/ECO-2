using System.Collections.Generic;
using System.IO;
using System.Text;
using Voxel.Core;

namespace Eco.Sim
{
    // Ecosystem save: everything needed for the simulation to resume exactly where it stopped,
    // including the state of the random generator.
    public sealed partial class Ecosystem
    {
        private const uint Magic = 0x454F4345; // "ECOE"
        private const int FormatVersion = 3;

        // Last format without animals. It still loads: animals then arrive from the map edges.
        private const int FormatWithoutAnimals = 2;

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);

                // Plants refer to their species by index: the list must be the same when loading.
                writer.Write(species.Length);
                foreach (Species kind in species)
                {
                    writer.Write(kind.Name);
                }

                // Plants stand on one specific terrain: that of this world, this seed and this generator
                // version.
                writer.Write(world.SizeX);
                writer.Write(world.SizeZ);
                writer.Write(world.Seed);
                writer.Write(world.GeneratorVersion);
                writer.Write(StepCount);
                writer.Write(random.State);
                writer.Write(nextPlantId);

                for (int i = 0; i < fertility.Length; i++)
                {
                    writer.Write(fertility[i]);
                    writer.Write(capacity[i]);
                }

                writer.Write(plantCount);
                for (int i = 0; i < plantCount; i++)
                {
                    Plant plant = plants[i];
                    writer.Write(plant.Id);
                    writer.Write(plant.SpeciesIndex);
                    writer.Write(plant.X);
                    writer.Write(plant.Y);
                    writer.Write(plant.Z);
                    writer.Write(plant.Age);
                    writer.Write(plant.Growth);
                }

                writer.Write(animalSpecies.Length);
                foreach (AnimalSpecies kind in animalSpecies)
                {
                    writer.Write(kind.Name);
                }

                writer.Write(nextAnimalId);
                writer.Write(animalCount);
                for (int i = 0; i < animalCount; i++)
                {
                    Animal animal = animals[i];
                    writer.Write(animal.Id);
                    writer.Write(animal.SpeciesIndex);
                    writer.Write(animal.X);
                    writer.Write(animal.Y);
                    writer.Write(animal.Z);
                    writer.Write(animal.Age);
                    writer.Write(animal.Hunger);
                }
            }
        }

        public static Ecosystem Read(Stream stream, World world, IReadOnlyList<Species> species)
        {
            return Read(stream, world, species, System.Array.Empty<AnimalSpecies>());
        }

        // Throws InvalidDataException if the file does not match this world or these species, and
        // EndOfStreamException if it is truncated.
        public static Ecosystem Read(Stream stream, World world, IReadOnlyList<Species> species, IReadOnlyList<AnimalSpecies> animalSpecies)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic)
                {
                    throw new InvalidDataException("This file is not an ecosystem save.");
                }

                int formatVersion = reader.ReadInt32();
                if (formatVersion != FormatVersion && formatVersion != FormatWithoutAnimals)
                {
                    throw new InvalidDataException(
                        $"Unsupported ecosystem save format {formatVersion} (expected: {FormatVersion}).");
                }

                int speciesCount = reader.ReadInt32();
                if (speciesCount != species.Count)
                {
                    throw new InvalidDataException("The species list has changed since this save.");
                }

                for (int i = 0; i < speciesCount; i++)
                {
                    string name = reader.ReadString();
                    if (name != species[i].Name)
                    {
                        throw new InvalidDataException(
                            $"The species list has changed since this save: '{name}' instead of '{species[i].Name}'.");
                    }
                }

                if (reader.ReadInt32() != world.SizeX || reader.ReadInt32() != world.SizeZ)
                {
                    throw new InvalidDataException("The ecosystem save does not match the world's dimensions.");
                }

                if (reader.ReadInt32() != world.Seed || reader.ReadInt32() != world.GeneratorVersion)
                {
                    throw new InvalidDataException("The ecosystem save was made on a different terrain than this world's.");
                }

                var ecosystem = new Ecosystem(world, species, animalSpecies, 0);
                ecosystem.StepCount = reader.ReadInt64();
                ecosystem.random.State = reader.ReadUInt64();
                ecosystem.nextPlantId = reader.ReadInt32();

                for (int i = 0; i < ecosystem.fertility.Length; i++)
                {
                    ecosystem.fertility[i] = reader.ReadSingle();
                    ecosystem.capacity[i] = reader.ReadSingle();
                }

                int savedCount = reader.ReadInt32();
                int previousId = 0;
                for (int i = 0; i < savedCount; i++)
                {
                    var plant = new Plant
                    {
                        Id = reader.ReadInt32(),
                        SpeciesIndex = reader.ReadInt32(),
                        X = reader.ReadInt32(),
                        Y = reader.ReadInt32(),
                        Z = reader.ReadInt32(),
                        Age = reader.ReadInt32(),
                        Growth = reader.ReadSingle()
                    };

                    // Plants are stored by increasing id, an order the rest of the code relies on.
                    bool valid = (uint)plant.SpeciesIndex < (uint)species.Count
                        && (uint)plant.X < (uint)world.SizeX
                        && (uint)plant.Z < (uint)world.SizeZ
                        && plant.Id > previousId;
                    if (!valid)
                    {
                        throw new InvalidDataException($"Invalid plant in the ecosystem save (id {plant.Id}).");
                    }

                    previousId = plant.Id;
                    ecosystem.Append(plant);
                }

                if (formatVersion != FormatWithoutAnimals) ecosystem.ReadAnimals(reader);
                return ecosystem;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static Ecosystem LoadFromFile(
            string path, World world, IReadOnlyList<Species> species, IReadOnlyList<AnimalSpecies> animalSpecies)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream, world, species, animalSpecies);
            }
        }

        private void ReadAnimals(BinaryReader reader)
        {
            int speciesCount = reader.ReadInt32();
            if (speciesCount != animalSpecies.Length)
            {
                throw new InvalidDataException("The animal species list has changed since this save.");
            }

            for (int i = 0; i < speciesCount; i++)
            {
                string name = reader.ReadString();
                if (name != animalSpecies[i].Name)
                {
                    throw new InvalidDataException(
                        $"The animal species list has changed since this save: '{name}' instead of '{animalSpecies[i].Name}'.");
                }
            }

            nextAnimalId = reader.ReadInt32();
            int savedCount = reader.ReadInt32();
            int previousId = 0;
            for (int i = 0; i < savedCount; i++)
            {
                int id = reader.ReadInt32();
                int speciesIndex = reader.ReadInt32();
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int z = reader.ReadInt32();
                int age = reader.ReadInt32();
                float hunger = reader.ReadSingle();

                bool valid = (uint)speciesIndex < (uint)animalSpecies.Length
                    && (uint)x < (uint)world.SizeX
                    && (uint)z < (uint)world.SizeZ
                    && id > previousId
                    && age >= 0
                    && animalAtColumn[ColumnIndex(x, z)] == 0;
                if (!valid)
                {
                    throw new InvalidDataException($"Invalid animal in the ecosystem save (id {id}).");
                }

                previousId = id;
                AddAnimal(id, speciesIndex, x, y, z, age, hunger);
            }
        }
    }
}
