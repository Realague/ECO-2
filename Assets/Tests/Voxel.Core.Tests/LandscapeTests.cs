using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class LandscapeTests
    {
        private const int Seed = 1;
        private const int Size = 512;

        private static Landscape Shared => Landscape.For(Seed, Size, Size);

        [Test]
        public void SameSeedAndSize_GiveTheSameLandscape_EvenAfterAnother()
        {
            int[] grounds = new int[64];
            int[] waters = new int[64];
            for (int i = 0; i < 64; i++)
            {
                grounds[i] = Shared.Ground(i * 7, i * 5);
                waters[i] = Shared.WaterTop(i * 7, i * 5);
            }

            Landscape.For(Seed + 1, Size, Size);
            Landscape again = Landscape.For(Seed, Size, Size);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(grounds[i], again.Ground(i * 7, i * 5));
                Assert.AreEqual(waters[i], again.WaterTop(i * 7, i * 5));
            }
        }

        [Test]
        public void TheWestEdge_IsSea_AtSeaLevel()
        {
            int sea = 0;
            for (int z = 0; z < Size; z++)
            {
                if (Shared.WaterTop(0, z) == Landscape.SeaLevel) sea++;
            }

            Assert.That(sea, Is.GreaterThan(Size * 9 / 10));
        }

        [Test]
        public void ThereAreLakesAndRivers_AndMostOfTheLandIsDry()
        {
            int wet = 0;
            int aboveSea = 0;
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int water = Shared.WaterTop(x, z);
                    if (water < 0) continue;

                    wet++;
                    if (water > Landscape.SeaLevel) aboveSea++;
                }
            }

            Assert.That(aboveSea, Is.GreaterThan(200), "rivers and lakes up in the land");
            Assert.That(wet, Is.LessThan(Size * Size / 4), "most of the land is dry");
        }

        // Water never stands higher than what holds it: next to each water column there is water,
        // or ground at least as high as the water's surface.
        [Test]
        public void Water_IsHeldByItsBanks()
        {
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int water = Shared.WaterTop(x, z);
                    if (water < 0) continue;

                    AssertHeld(x + 1, z, water, x, z);
                    AssertHeld(x - 1, z, water, x, z);
                    AssertHeld(x, z + 1, water, x, z);
                    AssertHeld(x, z - 1, water, x, z);
                }
            }
        }

        [Test]
        public void Water_IsAboveItsBed()
        {
            for (int z = 0; z < Size; z += 3)
            {
                for (int x = 0; x < Size; x += 3)
                {
                    int water = Shared.WaterTop(x, z);
                    if (water >= 0) Assert.That(water, Is.GreaterThan(Shared.Ground(x, z)), $"Column ({x}, {z})");
                }
            }
        }

        // Ground, water, surface and marshes of the whole landscape, recorded with version 6 of the
        // generator. If this fails, generation changed: bump TerrainGenerator.Version, then update it.
        [Test]
        public void Landscape_IsUnchangedSinceThisGeneratorVersion()
        {
            ulong hash = 14695981039346656037UL;
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Landscape.ColumnGeology column = Shared.Column(x, z);
                    int value = Shared.Ground(x, z) * 31 + Shared.WaterTop(x, z) * 7 + column.Top * 131 + column.Under * 1031 + (Shared.IsMarsh(x, z) ? 50000 : 0);
                    unchecked
                    {
                        hash = (hash ^ (uint)value) * 1099511628211UL;
                    }
                }
            }

            Assert.AreEqual(4095116596572024413UL, hash);
        }

        [Test]
        public void Marshes_CoverAFewPercent_AndHoldPools()
        {
            int marsh = 0;
            int pools = 0;
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (Shared.IsMarsh(x, z)) marsh++;
                    if (Shared.IsMarshPool(x, z)) pools++;
                }
            }

            Assert.That(marsh, Is.InRange(Size * Size / 100, Size * Size / 10), "marshes");
            Assert.That(pools * 5, Is.GreaterThan(marsh), "about a third of a marsh is pools");
            Assert.That(pools * 3, Is.LessThan(marsh * 2), "and the rest land");
        }

        // A marsh lies on low, very wet land that is not cold; its pools are one block deep with a
        // mud bed, its land grass on mud.
        [Test]
        public void Marshes_AreLowWetAndWarmEnough_WithShallowPools()
        {
            Geography climate = Shared.Geography;
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!Shared.IsMarsh(x, z)) continue;

                    string at = $"Marsh at ({x}, {z})";
                    bool pool = Shared.IsMarshPool(x, z);
                    int land = Shared.Ground(x, z) + (pool ? 1 : 0);
                    Assert.That(land, Is.LessThanOrEqualTo(Landscape.SeaLevel + 4), at);
                    Assert.That(climate.TemperatureAt(x, z), Is.GreaterThanOrEqualTo(IntNoise.One / 5), at);
                    Assert.That(climate.HumidityAt(x, z), Is.GreaterThan(IntNoise.One / 2), at);

                    Landscape.ColumnGeology column = Shared.Column(x, z);
                    if (pool)
                    {
                        Assert.AreEqual(Shared.Ground(x, z) + 1, Shared.WaterTop(x, z), at);
                        Assert.AreEqual(Blocks.Mud, column.Top, at);
                        Assert.IsFalse(Shared.IsIce(x, z), at);

                        // Beside it, pools at the same height or ground at least as high: never other water.
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int ox = x + dx, oz = z + dz;
                                if (Shared.IsMarshPool(ox, oz)) Assert.AreEqual(Shared.WaterTop(x, z), Shared.WaterTop(ox, oz), at);
                                else Assert.IsTrue(Shared.WaterTop(ox, oz) < 0 && Shared.Ground(ox, oz) >= Shared.WaterTop(x, z), at);
                            }
                        }
                    }
                    else
                    {
                        Assert.AreEqual(-1, Shared.WaterTop(x, z), at);
                        Assert.AreEqual(Blocks.Grass, column.Top, at);
                        Assert.AreEqual(Blocks.Mud, column.Under, at);
                    }
                }
            }
        }

        private static void AssertHeld(int x, int z, int water, int fromX, int fromZ)
        {
            if (x < 0 || z < 0 || x >= Size || z >= Size) return;

            bool held = Shared.WaterTop(x, z) >= 0 || Shared.Ground(x, z) >= water;
            Assert.IsTrue(held, $"Water at ({fromX}, {fromZ}) up to {water} spills over ({x}, {z}), ground {Shared.Ground(x, z)}");
        }
    }
}
