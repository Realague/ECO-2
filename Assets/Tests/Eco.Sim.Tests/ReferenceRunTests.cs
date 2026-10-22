using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    // A pinned run of the whole simulation on a generated world: every 250 steps the saved bytes (the
    // random state, the soil, every plant and animal with their exact values, the carcasses) and the
    // plant events since the last checkpoint are hashed and compared with the values recorded before.
    // It guards optimisations that must not change what a step does. The expected values change only
    // with an intended change of behaviour (a rule, a constant, the save format, the terrain): then
    // copy the new values from the failure message, saying why in the commit.
    public class ReferenceRunTests
    {
        private const int Seed = 1;
        private const int Steps = 1500;
        private const int CheckpointEvery = 250;
        private const int ReloadAt = 1000;

        private static readonly ulong[] Expected =
        {
            0xCFDDBA29E75A9F70UL, 0xB3CC002C7A13A036UL, 0xFB0954027FDA80AFUL, 0x81C32843E6A94DAEUL, 0xDA93FC43BB1668F9UL, 0xE411E23235271FF4UL
        };

        [Test]
        public void TheReferenceRun_GivesTheRecordedStates()
        {
            var world = new World(16, 8, 16);
            world.Generate(new TerrainGenerator(), Seed);
            Species[] plants = TestSpecies.ReferencePlants();
            AnimalSpecies[] animals = TestSpecies.ReferenceAnimals();

            var ecosystem = new Ecosystem(world, plants, animals, Seed);
            ecosystem.Populate(world.SizeX * world.SizeZ / 1024 * plants.Length);
            ecosystem.PopulateAnimals();

            var events = new EventLog();
            events.Listen(ecosystem);
            var actual = new List<ulong>();

            for (int step = 1; step <= Steps; step++)
            {
                Act(ecosystem, world, step);
                ecosystem.Step();

                if (step == ReloadAt)
                {
                    var saved = new MemoryStream();
                    ecosystem.Write(saved);
                    events.Forget(ecosystem);
                    ecosystem = Ecosystem.Read(new MemoryStream(saved.ToArray()), world, plants, animals);
                    events.Listen(ecosystem);
                }

                if (step % CheckpointEvery == 0) actual.Add(Checkpoint(ecosystem, events));
            }

            Assert.That(ecosystem.Plants.Count, Is.GreaterThan(500), "the run should keep a living world");
            Assert.That(ecosystem.Animals.Count, Is.GreaterThan(20));
            Assert.AreEqual(Expected, actual.ToArray(), "Actual: { " + string.Join(", ", actual.ConvertAll(h => "0x" + h.ToString("X16") + "UL")) + " }");
        }

        // Player actions at fixed steps, between two steps as on the server: felling, harvesting an
        // annual, killing an animal, a block placed under a plant, plants and animals placed by command,
        // trees felled to stumps (one sprouting again before the reload, one kept across it).
        private static void Act(Ecosystem ecosystem, World world, int step)
        {
            switch (step)
            {
                case 300:
                    ecosystem.Remove(ecosystem.Plants[ecosystem.Plants.Count / 2].Id);
                    break;
                case 350:
                case 800:
                    foreach (Plant plant in ecosystem.Plants)
                    {
                        if (plant.SpeciesIndex == 0 && plant.Growth >= 1f && !plant.IsStump)
                        {
                            ecosystem.CutToStump(plant.Id);
                            break;
                        }
                    }

                    break;
                case 400:
                    foreach (Plant plant in ecosystem.Plants)
                    {
                        if (plant.SpeciesIndex == 6 && plant.RipeStage >= 1)
                        {
                            ecosystem.TryHarvest(plant.Id, out _);
                            break;
                        }
                    }

                    break;
                case 500:
                    ecosystem.RemoveAnimal(ecosystem.Animals[ecosystem.Animals.Count / 3].Id);
                    break;
                case 600:
                    for (int i = 0; i < 3; i++)
                    {
                        Plant plant = ecosystem.Plants[ecosystem.Plants.Count * (i + 1) / 4];
                        world.SetBlock(plant.X, plant.Y - 1, plant.Z, Blocks.Granite);
                    }

                    break;
                case 700:
                    for (int x = 40, placed = 0; x < world.SizeX && placed < 4; x += 7)
                    {
                        if (ecosystem.TryPlaceGrownPlant(0, x, 128)) placed++;
                        if (ecosystem.TryPlaceAnimal(0, x, 132)) placed++;
                    }

                    break;
            }
        }

        private static ulong Checkpoint(Ecosystem ecosystem, EventLog events)
        {
            var stream = new MemoryStream();
            ecosystem.Write(stream);
            ulong hash = Fnv(Fnv(14695981039346656037UL, stream.ToArray()), events.Hash);
            events.Hash = 14695981039346656037UL;
            return hash;
        }

        private static ulong Fnv(ulong hash, byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }

            return hash;
        }

        private static ulong Fnv(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash = (hash ^ (byte)(value >> (8 * i))) * 1099511628211UL;
            }

            return hash;
        }

        // Every plant event in order: its kind, the plant and the stages it shows.
        private sealed class EventLog
        {
            public ulong Hash = 14695981039346656037UL;

            public void Listen(Ecosystem ecosystem)
            {
                ecosystem.PlantAdded += OnAdded;
                ecosystem.PlantChanged += OnChanged;
                ecosystem.PlantRemoved += OnRemoved;
            }

            public void Forget(Ecosystem ecosystem)
            {
                ecosystem.PlantAdded -= OnAdded;
                ecosystem.PlantChanged -= OnChanged;
                ecosystem.PlantRemoved -= OnRemoved;
            }

            private void OnAdded(Plant plant) => Add(1, plant);

            private void OnChanged(Plant plant) => Add(2, plant);

            private void OnRemoved(int plantId) => Hash = Fnv(Fnv(Hash, 3UL), (ulong)plantId);

            private void Add(ulong kind, Plant plant)
            {
                Hash = Fnv(Hash, kind);
                Hash = Fnv(Hash, (ulong)(uint)plant.Id | (ulong)plant.Stage << 32 | (ulong)plant.RipeStage << 40 | (plant.IsStump ? 1UL << 48 : 0UL));
            }
        }
    }
}
