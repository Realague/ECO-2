using System;
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
        private const int FormatVersion = 9;

        // Last format without stumps (a plant's age may now be negative: Plant.IsStump). Same layout:
        // it loads as is.
        private const int FormatWithoutStumps = 8;

        // Last format without animals' wounds (Animal.Damage). It still loads, every animal unhurt.
        private const int FormatWithoutDamage = 7;

        // Last format with a single fertility per cell instead of the soil's three nutrients. It still
        // loads: each nutrient starts as far below its capacity as the fertility was below its own.
        private const int FormatWithoutNutrients = 6;

        // Last format without crops' ripeness (Plant.Fruit). It still loads, every crop unripe.
        private const int FormatWithoutFruit = 5;

        // Last format without the seeds animals carry. It still loads, with none.
        private const int FormatWithoutCarriedSeeds = 4;

        // Last format without carcasses. It still loads, with none.
        private const int FormatWithoutCarrion = 3;

        // Last format without animals. It still loads: animals then arrive where their climate suits them.
        private const int FormatWithoutAnimals = 2;

        public void Write(Stream stream)
        {
            CompactRemoved();
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

                // The soil's levels; its capacities come from the land and are worked out again.
                for (int i = 0; i < soil.Length; i++)
                {
                    writer.Write(soil[i]);
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
                    writer.Write(plant.Fruit);
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
                    writer.Write(animal.CarriedSeed);
                    writer.Write(animal.Damage);
                }

                WriteCarrion(writer);
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
                if (formatVersion != FormatVersion && formatVersion != FormatWithoutStumps && formatVersion != FormatWithoutDamage && formatVersion != FormatWithoutNutrients && formatVersion != FormatWithoutFruit && formatVersion != FormatWithoutCarriedSeeds && formatVersion != FormatWithoutCarrion && formatVersion != FormatWithoutAnimals)
                {
                    throw new InvalidDataException(
                        $"Unsupported ecosystem save format {formatVersion} (expected: {FormatVersion}).");
                }

                // Species may have been added at the end of the list since the save, never removed or reordered.
                int speciesCount = reader.ReadInt32();
                if (speciesCount > species.Count)
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

                var ecosystem = new Ecosystem(world, species, animalSpecies, 0) { SavedSpeciesCount = speciesCount };
                ecosystem.StepCount = reader.ReadInt64();
                ecosystem.random.State = reader.ReadUInt64();
                ecosystem.nextPlantId = reader.ReadInt32();

                if (formatVersion > FormatWithoutNutrients) ecosystem.ReadSoil(reader);
                else ecosystem.ReadFertility(reader);

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

                    if (formatVersion > FormatWithoutFruit) plant.Fruit = reader.ReadSingle();

                    // Plants are stored by increasing id, an order the rest of the code relies on.
                    bool valid = (uint)plant.SpeciesIndex < (uint)speciesCount
                        && (uint)plant.X < (uint)world.SizeX
                        && (uint)plant.Z < (uint)world.SizeZ
                        && plant.Id > previousId
                        && plant.Fruit >= 0f && plant.Fruit <= 1f
                        && plant.Age >= -StumpRegrowSteps;
                    if (!valid)
                    {
                        throw new InvalidDataException($"Invalid plant in the ecosystem save (id {plant.Id}).");
                    }

                    previousId = plant.Id;
                    plant.Vigour = ecosystem.VigourAt(plant.SpeciesIndex, plant.X, plant.Z);
                    ecosystem.Append(plant);
                }

                if (formatVersion != FormatWithoutAnimals) ecosystem.ReadAnimals(reader, formatVersion > FormatWithoutCarriedSeeds, formatVersion > FormatWithoutDamage);
                if (formatVersion >= FormatWithoutCarriedSeeds) ecosystem.ReadCarrion(reader);
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

        private void ReadSoil(BinaryReader reader)
        {
            for (int i = 0; i < soil.Length; i++)
            {
                float level = reader.ReadSingle();
                if (!(level >= 0f && level <= soilCapacity[i] + MaxEnrichment + 0.01f))
                {
                    throw new InvalidDataException($"Invalid soil level in the ecosystem save (cell {i / NutrientCount}).");
                }

                soil[i] = level;
            }
        }

        // A save from before the soil had nutrients: a fertility and its capacity per cell. Each
        // nutrient starts as far below (or above) its own capacity as the fertility was.
        private void ReadFertility(BinaryReader reader)
        {
            int cells = soil.Length / NutrientCount;
            for (int cell = 0; cell < cells; cell++)
            {
                float fertility = reader.ReadSingle();
                float capacity = reader.ReadSingle();
                float share = capacity > 0f ? Math.Max(0f, fertility / capacity) : 1f;
                for (int n = 0; n < NutrientCount; n++)
                {
                    int index = cell * NutrientCount + n;
                    soil[index] = Math.Min(soilCapacity[index] + MaxEnrichment, soilCapacity[index] * share);
                }
            }
        }

        private void ReadAnimals(BinaryReader reader, bool withCarriedSeeds, bool withDamage)
        {
            // As for plants, species may have been added at the end of the list since the save.
            int speciesCount = reader.ReadInt32();
            if (speciesCount > animalSpecies.Length)
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
                int carriedSeed = withCarriedSeeds ? reader.ReadInt32() : 0;
                float damage = withDamage ? reader.ReadSingle() : 0f;

                bool valid = (uint)speciesIndex < (uint)animalSpecies.Length
                    && (uint)x < (uint)world.SizeX
                    && (uint)z < (uint)world.SizeZ
                    && id > previousId
                    && age >= 0
                    && animalAtColumn[ColumnIndex(x, z)] == 0
                    && carriedSeed >= 0 && carriedSeed <= species.Length
                    && damage >= 0f && damage < float.MaxValue;
                if (!valid)
                {
                    throw new InvalidDataException($"Invalid animal in the ecosystem save (id {id}).");
                }

                previousId = id;
                AddAnimal(id, speciesIndex, x, y, z, age, hunger);
                animals[animalCount - 1].CarriedSeed = carriedSeed;
                // A species given fewer hit points since the save does not kill its wounded on loading.
                animals[animalCount - 1].Damage = Math.Min(damage, animalSpecies[speciesIndex].Health * 0.99f);
            }
        }
    }
}
