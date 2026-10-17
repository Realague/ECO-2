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

        private static void AssertHeld(int x, int z, int water, int fromX, int fromZ)
        {
            if (x < 0 || z < 0 || x >= Size || z >= Size) return;

            bool held = Shared.WaterTop(x, z) >= 0 || Shared.Ground(x, z) >= water;
            Assert.IsTrue(held, $"Water at ({fromX}, {fromZ}) up to {water} spills over ({x}, {z}), ground {Shared.Ground(x, z)}");
        }
    }
}
