using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Voxel.Core;

namespace Eco.Sim.Tests
{
    public class AnimalTests
    {
        private const int Seed = 7;
        private const int BushIndex = 1;
        private const int RabbitIndex = 0;
        private const int FoxIndex = 1;

        [Test]
        public void HungryHerbivore_WalksToTheNearestBush_AndGrazesItWithoutKillingIt()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.11f), Fox());
            ecosystem.TryPlant(BushIndex, 10, 10);
            Run(ecosystem, 3 * SimClock.StepsPerDay);
            Assert.AreEqual(1f, ecosystem.Plants[0].Growth, "the bush is adult before the rabbit arrives");
            ecosystem.TryPlaceAnimal(RabbitIndex, 14, 10);

            Run(ecosystem, 2);

            Animal rabbit = ecosystem.Animals[0];
            Assert.AreEqual((11, 10), (rabbit.X, rabbit.Z), "it eats from beside the bush");
            Assert.AreEqual(0f, rabbit.Hunger);
            Assert.AreEqual(1, ecosystem.Plants.Count);
            Assert.AreEqual(0.5f, ecosystem.Plants[0].Growth, 0.001f);
        }

        [Test]
        public void Herbivore_IgnoresPlantsItDoesNotEat_AndBushesGrazedTooShort()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.06f, moveRange: 0), Fox());
            ecosystem.TryPlant(0, 11, 10);
            ecosystem.TryPlant(BushIndex, 10, 11);
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            Run(ecosystem, 3);

            Assert.That(ecosystem.Animals[0].Hunger, Is.GreaterThan(0.35f), "neither the tree nor the young shoot fed it");
        }

        [Test]
        public void Animal_StarvesWithoutFood()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.1f), Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            Run(ecosystem, 5);
            Assert.AreEqual(1, ecosystem.Animals.Count);
            Run(ecosystem, 3);

            Assert.AreEqual(0, ecosystem.Animals.Count);
            Assert.AreEqual(0, ecosystem.CountAnimals(RabbitIndex));
        }

        [Test]
        public void Animal_DiesOfOldAge()
        {
            AnimalSpecies shortLived = Rabbit();
            shortLived.LifespanSteps = 3;
            shortLived.MaturitySteps = 0;
            Ecosystem ecosystem = CreateEcosystem(shortLived, Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            Run(ecosystem, 3);
            Assert.AreEqual(1, ecosystem.Animals.Count);
            ecosystem.Step();

            Assert.AreEqual(0, ecosystem.Animals.Count);
        }

        [Test]
        public void HungryPredator_RunsToItsPrey_AndCatchesIt()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(moveRange: 0), Fox(hungerPerStep: 0.11f, catchChance: 1f));
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            ecosystem.TryPlaceAnimal(FoxIndex, 13, 10);

            ecosystem.Step();

            Assert.AreEqual(0, ecosystem.CountAnimals(RabbitIndex));
            Assert.AreEqual(1, ecosystem.CountAnimals(FoxIndex));
            Animal fox = ecosystem.Animals[0];
            Assert.AreEqual((11, 10), (fox.X, fox.Z), "the fox stops next to its prey, not on it");
            Assert.AreEqual(0f, fox.Hunger);
        }

        [Test]
        public void Predator_ThatIsNotHungry_LeavesItsPreyAlone()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(moveRange: 0), Fox(moveRange: 0, catchChance: 1f));
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            ecosystem.TryPlaceAnimal(FoxIndex, 11, 10);

            Run(ecosystem, 20);

            Assert.AreEqual(1, ecosystem.CountAnimals(RabbitIndex));
        }

        [Test]
        public void WellFedAdult_GivesBirthNextToItself_AndGetsHungrier()
        {
            AnimalSpecies prolific = Rabbit(moveRange: 0);
            prolific.BirthChance = 1f;
            prolific.MaturitySteps = 5;
            Ecosystem ecosystem = CreateEcosystem(prolific, Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            ecosystem.Step();

            Assert.AreEqual(2, ecosystem.Animals.Count);
            Animal parent = ecosystem.Animals[0];
            Animal young = ecosystem.Animals[1];
            Assert.That(parent.Hunger, Is.GreaterThan(0.4f));
            Assert.AreEqual(0, young.Age);
            Assert.AreEqual(1, Math.Max(Math.Abs(young.X - parent.X), Math.Abs(young.Z - parent.Z)));

            Run(ecosystem, 3);
            Assert.AreEqual(2, ecosystem.Animals.Count, "the parent is too hungry to breed again, the young is too young");
        }

        [Test]
        public void Animal_DoesNotBreed_WhenItsTerritoryIsAlreadyFull()
        {
            AnimalSpecies territorial = Rabbit(moveRange: 0);
            territorial.BirthChance = 1f;
            territorial.MaturitySteps = 5;
            territorial.TerritoryRange = 4;
            territorial.MaxNeighbours = 1;
            Ecosystem ecosystem = CreateEcosystem(territorial, Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            ecosystem.TryPlaceAnimal(RabbitIndex, 12, 10);
            ecosystem.TryPlaceAnimal(RabbitIndex, 25, 25);

            ecosystem.Step();

            Assert.AreEqual(4, ecosystem.Animals.Count, "only the isolated rabbit has a young");
            Animal young = ecosystem.Animals[3];
            Assert.AreEqual(1, Math.Max(Math.Abs(young.X - 25), Math.Abs(young.Z - 25)));
        }

        [Test]
        public void Animals_StayInsideTheWorld_MoveWithinTheirRange_AndNeverShareAColumn()
        {
            World world = MeadowGenerator.CreateWorld(1);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeAndBush(), new[] { Rabbit(moveRange: 3), Fox() }, Seed);
            for (int i = 0; i < 40; i++)
            {
                ecosystem.TryPlaceAnimal(RabbitIndex, i % 8, i / 8);
            }

            Dictionary<int, (int X, int Z)> before = ecosystem.Animals.ToDictionary(animal => animal.Id, animal => (animal.X, animal.Z));
            for (int step = 0; step < 60; step++)
            {
                ecosystem.Step();

                var occupied = new HashSet<(int, int)>();
                foreach (Animal animal in ecosystem.Animals)
                {
                    Assert.IsTrue(world.InBounds(animal.X, animal.Y, animal.Z), $"Animal {animal.Id} outside the world");
                    Assert.AreEqual(MeadowGenerator.Surface, animal.Y);
                    Assert.IsTrue(occupied.Add((animal.X, animal.Z)), $"Two animals at ({animal.X}, {animal.Z})");

                    (int X, int Z) previous = before[animal.Id];
                    Assert.That(Math.Max(Math.Abs(animal.X - previous.X), Math.Abs(animal.Z - previous.Z)), Is.LessThanOrEqualTo(3));
                    before[animal.Id] = (animal.X, animal.Z);
                }
            }

            Assert.AreEqual(40, ecosystem.Animals.Count);
        }

        [Test]
        public void HungryHerbivore_ClimbsAOneBlockStep_ToReachFood()
        {
            World world = MeadowGenerator.CreateWorld(2);
            RaiseGround(world, 16, 1);
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.11f, moveRange: 3), Fox(), world);
            Assert.IsTrue(ecosystem.TryPlant(BushIndex, 18, 16));
            Run(ecosystem, 3 * SimClock.StepsPerDay);
            ecosystem.TryPlaceAnimal(RabbitIndex, 13, 16);

            // Three columns on the first step, the last one up the step; next to the bush on the second.
            Run(ecosystem, 2);

            Animal rabbit = ecosystem.Animals[0];
            Assert.That(rabbit.X, Is.GreaterThanOrEqualTo(16), "it went up onto the step");
            Assert.AreEqual(MeadowGenerator.Surface + 1, rabbit.Y);
            Assert.AreEqual(0.5f, ecosystem.Plants[0].Growth, 0.001f, "it grazed the bush");
        }

        [Test]
        public void HungryHerbivore_CannotClimbATwoBlockWall_ToReachFood()
        {
            World world = MeadowGenerator.CreateWorld(2);
            RaiseGround(world, 16, 2);
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.11f, moveRange: 3), Fox(), world);
            Assert.IsTrue(ecosystem.TryPlant(BushIndex, 18, 16));
            Run(ecosystem, 3 * SimClock.StepsPerDay);
            ecosystem.TryPlaceAnimal(RabbitIndex, 13, 16);

            Run(ecosystem, 5);

            Assert.That(ecosystem.Animals[0].X, Is.LessThan(16));
            Assert.AreEqual(1f, ecosystem.Plants[0].Growth, "the bush is out of reach");
        }

        [Test]
        public void WanderingAnimals_NeverCrossAWallTwoBlocksHigh()
        {
            World world = MeadowGenerator.CreateWorld(2);
            for (int z = 0; z < world.SizeZ; z++)
            {
                world.SetBlock(16, MeadowGenerator.Surface, z, Blocks.Stone);
                world.SetBlock(16, MeadowGenerator.Surface + 1, z, Blocks.Stone);
            }

            Ecosystem ecosystem = CreateEcosystem(Rabbit(moveRange: 3), Fox(), world);
            for (int z = 2; z < 30; z += 4)
            {
                ecosystem.TryPlaceAnimal(RabbitIndex, 14, z);
            }

            for (int step = 0; step < 300; step++)
            {
                ecosystem.Step();
                foreach (Animal animal in ecosystem.Animals)
                {
                    Assert.That(animal.X, Is.LessThan(16), $"step {step}: animal {animal.Id} crossed the wall");
                }
            }
        }

        [Test]
        public void IsAnimalInside_TellsTheBlockItsBodyTakesUp()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(moveRange: 0), Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            Assert.IsTrue(ecosystem.IsAnimalInside(10, MeadowGenerator.Surface, 10));
            Assert.IsFalse(ecosystem.IsAnimalInside(10, MeadowGenerator.Surface + 1, 10), "a rabbit is less than one block high");
            Assert.IsFalse(ecosystem.IsAnimalInside(11, MeadowGenerator.Surface, 10));
            Assert.IsFalse(ecosystem.IsAnimalInside(-1, MeadowGenerator.Surface, 10));
        }

        [Test]
        public void Animal_FollowsTheGround_WhenItIsDugOrRaisedUnderIt()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var ecosystem = new Ecosystem(world, TestSpecies.TreeAndBush(), new[] { Rabbit(moveRange: 0), Fox() }, Seed);
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);

            world.SetBlock(10, MeadowGenerator.Surface - 1, 10, Blocks.Air);
            ecosystem.Step();
            Assert.AreEqual(MeadowGenerator.Surface - 1, ecosystem.Animals[0].Y);

            world.SetBlock(10, MeadowGenerator.Surface - 1, 10, Blocks.Dirt);
            world.SetBlock(10, MeadowGenerator.Surface, 10, Blocks.Dirt);
            ecosystem.Step();
            Assert.AreEqual(MeadowGenerator.Surface + 1, ecosystem.Animals[0].Y);
        }

        [Test]
        public void RemoveAnimal_KillsIt_AndUnknownIdsAreIgnored()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(), Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10);
            ecosystem.TryPlaceAnimal(FoxIndex, 20, 20);
            int rabbit = ecosystem.Animals[0].Id;
            int fox = ecosystem.Animals[1].Id;

            Assert.IsTrue(ecosystem.RemoveAnimal(rabbit));
            Assert.IsFalse(ecosystem.RemoveAnimal(rabbit));
            Assert.IsFalse(ecosystem.RemoveAnimal(9999));

            Assert.IsFalse(ecosystem.TryGetAnimal(rabbit, out _));
            Assert.IsTrue(ecosystem.TryGetAnimal(fox, out Animal survivor));
            Assert.AreEqual(20, survivor.X);
            Assert.IsTrue(ecosystem.TryPlaceAnimal(RabbitIndex, 10, 10), "the rabbit's column is free again");
        }

        [Test]
        public void Herbivore_OnlyEatsPlantsWithinItsReach()
        {
            AnimalSpecies shortRabbit = Rabbit(hungerPerStep: 0.11f);
            shortRabbit.ReachHeight = 0.5f;
            Ecosystem ecosystem = CreateEcosystem(shortRabbit, Fox());
            ecosystem.TryPlant(BushIndex, 10, 10);
            Run(ecosystem, 3 * SimClock.StepsPerDay);
            ecosystem.TryPlaceAnimal(RabbitIndex, 12, 10);

            Run(ecosystem, 3);

            Assert.That(ecosystem.Animals[0].Hunger, Is.GreaterThan(0.3f), "the grown bush, 0.9 high, is out of its reach");
            Assert.AreEqual(1f, ecosystem.Plants[0].Growth);
        }

        [Test]
        public void GrazingAPlantThatCannotSpareIt_KillsIt()
        {
            Ecosystem ecosystem = CreateEcosystem(Rabbit(hungerPerStep: 0.3f), Fox());
            ecosystem.TryPlant(BushIndex, 10, 10);
            Run(ecosystem, 3 * SimClock.StepsPerDay);
            ecosystem.TryPlaceAnimal(RabbitIndex, 11, 10);

            Run(ecosystem, 4);

            Assert.AreEqual(0, ecosystem.Plants.Count, "grazed twice, the bush had nothing left to lose");
        }

        [Test]
        public void HerdAnimals_GatherNearOneAnother()
        {
            AnimalSpecies deer = Rabbit();
            deer.Herd = true;
            deer.SenseRange = 10;
            Ecosystem ecosystem = CreateEcosystem(deer, Fox());
            ecosystem.TryPlaceAnimal(RabbitIndex, 5, 10);
            ecosystem.TryPlaceAnimal(RabbitIndex, 13, 10);

            Run(ecosystem, 10);

            Animal first = ecosystem.Animals[0];
            Animal second = ecosystem.Animals[1];
            Assert.That(Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Z - second.Z)), Is.LessThanOrEqualTo(3));
        }

        // The first animals and the newcomers only appear where the climate suits their species.
        [Test]
        public void Animals_OnlySettleWhereTheClimateSuitsThem()
        {
            World world = MeadowGenerator.CreateWorld(4);
            var warmths = new List<float>();
            for (int z = 0; z < world.SizeZ; z += 4)
            {
                for (int x = 0; x < world.SizeX; x += 4)
                {
                    warmths.Add((float)world.Geography.TemperatureAt(x, z) / IntNoise.One);
                }
            }

            warmths.Sort();
            AnimalSpecies cold = Rabbit();
            cold.MinDensity = 100f;
            cold.WarmthMax = warmths[warmths.Count / 2];
            cold.ClimateTolerance = 0.001f;
            var ecosystem = new Ecosystem(world, TestSpecies.TreeAndBush(), new[] { cold }, Seed);

            ecosystem.PopulateAnimals();

            Assert.That(ecosystem.Animals.Count, Is.GreaterThan(5));
            foreach (Animal animal in ecosystem.Animals)
            {
                Assert.That(ecosystem.AnimalVigourAt(RabbitIndex, animal.X, animal.Z), Is.GreaterThanOrEqualTo(0.5f), $"Animal at ({animal.X}, {animal.Z})");
            }

            // Wandering, they keep mostly to their climate.
            Run(ecosystem, 200);
            int suited = ecosystem.Animals.Count(animal => ecosystem.AnimalVigourAt(RabbitIndex, animal.X, animal.Z) > 0f);
            Assert.That(suited, Is.GreaterThanOrEqualTo(ecosystem.Animals.Count * 8 / 10));
        }

        [Test]
        public void SpeciesBelowItsMinimum_ComesBack()
        {
            AnimalSpecies returning = Rabbit();
            returning.MinDensity = 100f;
            Ecosystem ecosystem = CreateEcosystem(returning, Fox());
            Assert.AreEqual(0, ecosystem.Animals.Count);

            Run(ecosystem, 400);

            Assert.That(ecosystem.CountAnimals(RabbitIndex), Is.GreaterThan(0));
            Assert.AreEqual(0, ecosystem.CountAnimals(FoxIndex), "the fox, with no minimum, does not come back");
        }

        [Test]
        public void SameSeedAndSameActions_GiveTheSameAnimals()
        {
            Ecosystem first = CreateLivingWorld();
            Ecosystem second = CreateLivingWorld();

            Run(first, 1500);
            Run(second, 1500);

            Assert.That(first.Animals.Count, Is.GreaterThan(0));
            AssertSameAnimals(first, second);
        }

        [Test]
        public void WriteThenRead_ResumesExactlyWhereItStopped_AnimalsIncluded()
        {
            World world = MeadowGenerator.CreateWorld(2);
            Ecosystem original = CreateLivingWorld(world);
            Run(original, 600);

            var stream = new MemoryStream();
            original.Write(stream);
            stream.Position = 0;
            Ecosystem loaded = Ecosystem.Read(stream, world, TestSpecies.TreeAndBush(), LivingSpecies());

            AssertSameAnimals(original, loaded);
            Run(original, 600);
            Run(loaded, 600);
            AssertSameAnimals(original, loaded);
            Assert.AreEqual(original.Plants.Count, loaded.Plants.Count);
        }

        [Test]
        public void Read_SaveFromBeforeAnimalsExisted_LoadsThePlants_AndStartsWithoutAnimals()
        {
            World world = MeadowGenerator.CreateWorld(2);
            var withoutAnimals = new Ecosystem(world, TestSpecies.TreeAndBush(), Seed);
            withoutAnimals.Populate(40);
            var stream = new MemoryStream();
            withoutAnimals.Write(stream);

            // The old format stopped after the plants, which had no ripeness: the animal and carcass
            // sections are removed (four integers for an ecosystem without animal species) and the old
            // format number is put back.
            byte[] data = OldSaves.WithoutFruit(stream.ToArray(), world);
            var oldFormat = new byte[data.Length - 4 * sizeof(int)];
            Array.Copy(data, oldFormat, oldFormat.Length);
            BitConverter.GetBytes(2).CopyTo(oldFormat, sizeof(uint));

            Ecosystem loaded = Ecosystem.Read(new MemoryStream(oldFormat), world, TestSpecies.TreeAndBush(), LivingSpecies());

            Assert.AreEqual(withoutAnimals.Plants.Count, loaded.Plants.Count);
            Assert.AreEqual(0, loaded.Animals.Count);
        }

        [Test]
        public void Read_SaveWithOtherAnimalSpecies_Throws()
        {
            World world = MeadowGenerator.CreateWorld(2);
            Ecosystem original = CreateLivingWorld(world);
            var stream = new MemoryStream();
            original.Write(stream);
            byte[] data = stream.ToArray();

            Assert.Throws<InvalidDataException>(
                () => Ecosystem.Read(new MemoryStream(data), world, TestSpecies.TreeAndBush(), new[] { Rabbit() }));
        }

        private static AnimalSpecies Rabbit(float hungerPerStep = 0f, int moveRange = 2)
        {
            return new AnimalSpecies
            {
                Name = "Rabbit",
                EatsPlants = new[] { BushIndex },
                MoveRange = moveRange,
                SenseRange = 5,
                LifespanSteps = 15 * SimClock.StepsPerDay,
                MaturitySteps = SimClock.StepsPerDay,
                HungerPerStep = hungerPerStep,
                MealValue = 0.5f,
                Height = 0.6f,
                Width = 0.6f
            };
        }

        private static AnimalSpecies Fox(float hungerPerStep = 0f, int moveRange = 2, float catchChance = 0.5f)
        {
            return new AnimalSpecies
            {
                Name = "Fox",
                EatsAnimals = new[] { RabbitIndex },
                MoveRange = moveRange,
                SenseRange = 6,
                LifespanSteps = 30 * SimClock.StepsPerDay,
                MaturitySteps = 3 * SimClock.StepsPerDay,
                HungerPerStep = hungerPerStep,
                MealValue = 0.5f,
                CatchChance = catchChance,
                Height = 0.8f,
                Width = 0.8f
            };
        }

        // Species that eat, breed and come back from outside, for long-running tests.
        private static AnimalSpecies[] LivingSpecies()
        {
            AnimalSpecies rabbit = Rabbit(hungerPerStep: 0.004f);
            rabbit.BirthChance = 0.02f;
            rabbit.MinDensity = 20f;

            AnimalSpecies fox = Fox(hungerPerStep: 0.002f);
            fox.BirthChance = 0.004f;
            fox.MinDensity = 5f;
            return new[] { rabbit, fox };
        }

        private static Ecosystem CreateLivingWorld(World world = null)
        {
            var ecosystem = new Ecosystem(world ?? MeadowGenerator.CreateWorld(2), TestSpecies.TreeAndBush(), LivingSpecies(), Seed);
            ecosystem.Populate(60);
            ecosystem.PopulateAnimals();
            return ecosystem;
        }

        // A world without seeds: tests place the plants they need themselves.
        private static Ecosystem CreateEcosystem(AnimalSpecies rabbit, AnimalSpecies fox, World world = null)
        {
            Species tree = TestSpecies.Tree();
            tree.SeedChance = 0f;
            Species bush = TestSpecies.Bush();
            bush.SeedChance = 0f;
            bush.MinSoil = 0.2f;
            return new Ecosystem(world ?? MeadowGenerator.CreateWorld(2), new[] { tree, bush }, new[] { rabbit, fox }, Seed);
        }

        // Fills every column from x = fromX to the world's edge with dirt, `blocks` blocks above the meadow.
        private static void RaiseGround(World world, int fromX, int blocks)
        {
            for (int x = fromX; x < world.SizeX; x++)
            {
                for (int z = 0; z < world.SizeZ; z++)
                {
                    for (int y = MeadowGenerator.Surface; y < MeadowGenerator.Surface + blocks; y++)
                    {
                        world.SetBlock(x, y, z, Blocks.Dirt);
                    }
                }
            }
        }

        private static void Run(Ecosystem ecosystem, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                ecosystem.Step();
            }
        }

        private static void AssertSameAnimals(Ecosystem expected, Ecosystem actual)
        {
            Assert.AreEqual(expected.StepCount, actual.StepCount);
            Assert.AreEqual(expected.Animals.Count, actual.Animals.Count);
            for (int i = 0; i < expected.Animals.Count; i++)
            {
                Animal a = expected.Animals[i];
                Animal b = actual.Animals[i];
                Assert.AreEqual((a.Id, a.SpeciesIndex, a.X, a.Y, a.Z, a.Age, a.Hunger), (b.Id, b.SpeciesIndex, b.X, b.Y, b.Z, b.Age, b.Hunger));
            }
        }
    }
}
