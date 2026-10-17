using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class CavesTests
    {
        private const int Seed = 1;
        private const int Size = 512;

        private static World world;

        private static Landscape Shared => Landscape.For(Seed, Size, Size);

        // A 512 × 512 world, generated once for the tests that read blocks.
        private static World SharedWorld
        {
            get
            {
                if (world == null)
                {
                    world = new World(Size / Chunk.Size, 8, Size / Chunk.Size);
                    world.Generate(new TerrainGenerator(), Seed);
                }

                return world;
            }
        }

        [Test]
        public void World_HasTunnelsCavernsEntrancesAndLakes()
        {
            Caves caves = Shared.Caves;

            Assert.That(caves.HollowCount, Is.GreaterThan(5000));
            Assert.That(caves.CavernCount, Is.GreaterThan(20));
            Assert.That(caves.EntranceCount, Is.GreaterThan(10));
            Assert.That(caves.LakeCount, Is.GreaterThan(5));
        }

        [Test]
        public void SameSeedAndSize_GiveTheSameCaves_AndAnotherSeedOthers()
        {
            Caves first = Shared.Caves;
            var again = new Caves(Seed, Shared, Size, Size);
            var other = new Caves(Seed + 1, Landscape.For(Seed + 1, Size, Size), Size, Size);

            int differences = 0;
            for (int z = 0; z < Size; z += 3)
            {
                for (int x = 0; x < Size; x += 3)
                {
                    for (int y = 3; y < 40; y += 2)
                    {
                        Assert.AreEqual(first.IsHollow(x, y, z), again.IsHollow(x, y, z), $"({x}, {y}, {z})");
                        if (first.IsHollow(x, y, z) != other.IsHollow(x, y, z)) differences++;
                    }
                }
            }

            Assert.That(differences, Is.GreaterThan(100));
        }

        // Bedrock and the block above it are never dug, nor anything less than four blocks under
        // surface water or beside it: lakes, rivers and the sea never stand over a void.
        [Test]
        public void Caves_KeepTheBottomOfTheWorldAndARoofUnderSurfaceWater()
        {
            Landscape landscape = Shared;
            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    for (int y = 0; y <= 2; y++)
                    {
                        Assert.IsFalse(landscape.Caves.IsHollow(x, y, z), $"Bottom at ({x}, {y}, {z})");
                    }

                    if (landscape.WaterTop(x, z) < 0) continue;

                    for (int dz = -2; dz <= 2; dz++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int nx = x + dx;
                            int nz = z + dz;
                            if (nx < 0 || nz < 0 || nx >= Size || nz >= Size) continue;

                            for (int y = landscape.Ground(x, z) - 3; y <= landscape.Ground(x, z); y++)
                            {
                                Assert.IsFalse(landscape.Caves.IsHollow(nx, y, nz), $"Under water near ({x}, {z}): ({nx}, {y}, {nz})");
                            }
                        }
                    }
                }
            }
        }

        // Cave water is always held: below and beside each block of a cave lake there is more of
        // the lake or solid rock, and it never stands in the open air.
        [Test]
        public void CaveLakes_AreHeldByRock()
        {
            Landscape landscape = Shared;
            World generated = SharedWorld;
            int lakeBlocks = 0;

            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int ground = landscape.Ground(x, z);
                    for (int y = 3; y <= ground; y++)
                    {
                        if (!landscape.Caves.IsLake(x, y, z)) continue;

                        lakeBlocks++;
                        Assert.AreEqual(Blocks.Water, generated.GetBlock(x, y, z));
                        AssertHolds(generated, x, y - 1, z);
                        AssertHolds(generated, x + 1, y, z);
                        AssertHolds(generated, x - 1, y, z);
                        AssertHolds(generated, x, y, z + 1);
                        AssertHolds(generated, x, y, z - 1);
                    }
                }
            }

            Assert.That(lakeBlocks, Is.GreaterThan(500));
        }

        private static void AssertHolds(World generated, int x, int y, int z)
        {
            ushort block = generated.GetBlock(x, y, z);
            Assert.IsTrue(Blocks.IsSolid(block) || Blocks.IsWater(block) || !generated.InBounds(x, y, z), $"Cave water leaks at ({x}, {y}, {z})");
        }
    }
}
