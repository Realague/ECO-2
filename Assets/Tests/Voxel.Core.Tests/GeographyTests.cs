using System;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class GeographyTests
    {
        private const int Seed = 1;
        private const int Size = 512;

        private static readonly Geography Shared = Geography.Create(Seed, Size, Size);

        // Values recorded when the climate was introduced. If this test fails, the climate has changed:
        // fertility, and the grass colour every machine works out, change with it. Make sure that is
        // wanted, then update these values.
        [TestCase(1, 0, 0, 45641, 23961)]
        [TestCase(1, 100, 400, 8908, 50980)]
        [TestCase(1, 256, 256, 37107, 50780)]
        [TestCase(1, 511, 30, 61937, 43941)]
        [TestCase(42, 37, 511, 9519, 64344)]
        [TestCase(42, 300, 123, 25556, 34603)]
        public void Climate_IsUnchanged(int seed, int x, int z, int temperature, int humidity)
        {
            Geography geography = Geography.Create(seed, Size, Size);
            Assert.AreEqual(temperature, geography.TemperatureAt(x, z), "temperature");
            Assert.AreEqual(humidity, geography.HumidityAt(x, z), "humidity");
        }

        [Test]
        public void SameSeedAndSize_GiveTheSameClimate()
        {
            Geography other = Geography.Create(Seed, Size, Size);
            for (int z = 0; z < Size; z += 37)
            {
                for (int x = 0; x < Size; x += 29)
                {
                    Assert.AreEqual(Shared.TemperatureAt(x, z), other.TemperatureAt(x, z), $"Temperature at ({x}, {z})");
                    Assert.AreEqual(Shared.HumidityAt(x, z), other.HumidityAt(x, z), $"Humidity at ({x}, {z})");
                }
            }
        }

        [Test]
        public void DifferentSeeds_GiveDifferentClimates()
        {
            Geography other = Geography.Create(Seed + 1, Size, Size);
            int differences = 0;
            for (int x = 0; x < Size; x += 16)
            {
                if (Shared.HumidityAt(x, 200) != other.HumidityAt(x, 200)) differences++;
            }

            Assert.That(differences, Is.GreaterThan(16));
        }

        [Test]
        public void Values_StayWithinTheirRange()
        {
            for (int cellZ = 0; cellZ < Shared.CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < Shared.CellsX; cellX++)
                {
                    Assert.That(Shared.Temperature(cellX, cellZ), Is.InRange(0, IntNoise.One - 1));
                    Assert.That(Shared.Humidity(cellX, cellZ), Is.InRange(0, IntNoise.One - 1));
                }
            }

            // Outside the world the edge values are held.
            Assert.AreEqual(Shared.TemperatureAt(0, 0), Shared.TemperatureAt(-50, -50));
            Assert.AreEqual(Shared.HumidityAt(Size - 1, Size - 1), Shared.HumidityAt(Size + 50, Size + 50));
        }

        [Test]
        public void South_IsWarmerThanNorth()
        {
            long south = 0;
            long north = 0;
            for (int x = 0; x < Size; x += 8)
            {
                for (int z = 0; z < Size / 4; z += 8)
                {
                    south += Shared.TemperatureAt(x, z);
                    north += Shared.TemperatureAt(x, Size - 1 - z);
                }
            }

            Assert.That(south, Is.GreaterThan(north * 3 / 2));
        }

        // A plain at height 22 crossed from south to north by a ridge 40 blocks high around x = 256,
        // compared with the same plain without it: the zones' noise is the same in both.
        private const int Plain = 22;
        private const int RidgeX = 256;

        private static readonly Geography FlatLand = Geography.Create(Seed, Size, Size, (x, z) => Plain);

        private static readonly Geography Ridge =
            Geography.Create(Seed, Size, Size, (x, z) => Plain + Math.Max(0, 40 - Math.Abs(x - RidgeX)));

        [Test]
        public void HighGround_IsColderThanLowGround()
        {
            for (int z = 16; z < Size; z += 32)
            {
                Assert.That(Ridge.TemperatureAt(RidgeX, z), Is.LessThan(FlatLand.TemperatureAt(RidgeX, z)), $"Crest at z = {z}");
                Assert.AreEqual(FlatLand.TemperatureAt(100, z), Ridge.TemperatureAt(100, z), $"Plain at z = {z}");
            }
        }

        // The wind blows from the west: the slope facing it gets the rain, the land past the crest stays dry.
        [Test]
        public void LeeSideOfARidge_IsDrier_AndItsWindwardSlopeWetter()
        {
            long windwardRidge = 0, windwardFlat = 0, leeRidge = 0, leeFlat = 0;
            for (int z = 16; z < Size; z += 16)
            {
                windwardRidge += Ridge.HumidityAt(RidgeX - 20, z);
                windwardFlat += FlatLand.HumidityAt(RidgeX - 20, z);
                leeRidge += Ridge.HumidityAt(RidgeX + 80, z);
                leeFlat += FlatLand.HumidityAt(RidgeX + 80, z);
            }

            Assert.That(windwardRidge, Is.GreaterThan(windwardFlat), "rain on the slope facing the wind");
            Assert.That(leeRidge, Is.LessThan(leeFlat * 3 / 4), "dry land past the crest");
        }

        [Test]
        public void Climate_ChangesSmoothlyFromOneColumnToTheNext()
        {
            int largestStep = 0;
            for (int z = 0; z < Size; z += 13)
            {
                for (int x = 0; x < Size - 1; x++)
                {
                    largestStep = Math.Max(largestStep, Math.Abs(Shared.TemperatureAt(x + 1, z) - Shared.TemperatureAt(x, z)));
                    largestStep = Math.Max(largestStep, Math.Abs(Shared.HumidityAt(x + 1, z) - Shared.HumidityAt(x, z)));
                }
            }

            Assert.That(largestStep, Is.LessThan(IntNoise.One / 8));
        }

        [Test]
        public void World_GivesTheGeographyOfItsSeedAndSize()
        {
            var world = new World(2, 1, 3);
            world.Generate(new FlatGenerator(), 7);

            Geography geography = world.Geography;
            Assert.AreSame(geography, world.Geography, "worked out once");
            Assert.AreEqual(32 / Geography.CellSize, geography.CellsX);
            Assert.AreEqual(48 / Geography.CellSize, geography.CellsZ);
            Assert.AreEqual(Geography.Create(7, 32, 48).HumidityAt(10, 20), geography.HumidityAt(10, 20));
        }
    }
}
