using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class TerrainGeneratorTests
    {
        private const int Seed = 1;

        // Values recorded with version 2 of the generator. If this test fails, the produced terrain has
        // changed: existing saves no longer match and clients on another version
        // would generate another world. Bump TerrainGenerator.Version, then update these values.
        [TestCase(0, 0, 1, 25)]
        [TestCase(64, 64, 1, 39)]
        [TestCase(500, -300, 1, 25)]
        [TestCase(-1000, 777, 1, 28)]
        [TestCase(12345, 6789, 1, 25)]
        [TestCase(100, 900, 1, 40)]
        [TestCase(64, 64, 42, 35)]
        public void SurfaceHeight_IsUnchangedSinceThisGeneratorVersion(int x, int z, int seed, int expected)
        {
            Assert.AreEqual(2, new TerrainGenerator().Version, "reference values to update for the new version");
            Assert.AreEqual(expected, TerrainGenerator.SurfaceHeight(x, z, seed));
        }

        [Test]
        public void GeneratedWorld_IsUnchangedSinceThisGeneratorVersion()
        {
            Assert.AreEqual(16141247128516064775UL, CreateWorld().TerrainFingerprint);
        }

        [Test]
        public void DifferentSeeds_GiveDifferentTerrain()
        {
            int differences = 0;
            for (int x = 0; x < 256; x += 4)
            {
                if (TerrainGenerator.SurfaceHeight(x, 100, 1) != TerrainGenerator.SurfaceHeight(x, 100, 2)) differences++;
            }

            Assert.That(differences, Is.GreaterThan(16));
        }

        [Test]
        public void Terrain_HasFlatLandAndHighGround_AndFitsInASixtyFourBlockHighWorld()
        {
            int lowest = int.MaxValue;
            int highest = int.MinValue;
            for (int z = -512; z < 512; z += 3)
            {
                for (int x = -512; x < 512; x += 3)
                {
                    int height = TerrainGenerator.SurfaceHeight(x, z, Seed);
                    if (height < lowest) lowest = height;
                    if (height > highest) highest = height;
                }
            }

            Assert.That(lowest, Is.GreaterThanOrEqualTo(20), "the stone base stays intact under the relief");
            Assert.That(highest, Is.LessThan(62), "the relief fits under the world's ceiling");
            Assert.That(highest - lowest, Is.GreaterThan(20), "there are plains and massifs");
        }

        [Test]
        public void EveryColumn_IsSolidUpToItsSurface_WithGrassOnDirtOrBareRock()
        {
            World world = CreateWorld();

            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int x = 0; x < world.SizeX; x++)
                {
                    int top = TerrainGenerator.SurfaceHeight(x, z, Seed);
                    Assert.AreEqual(top + 1, world.GetSurfaceHeight(x, z), $"Column ({x}, {z})");

                    for (int y = 0; y <= top; y++)
                    {
                        Assert.AreNotEqual(Blocks.Air, world.GetBlock(x, y, z), $"Hole at ({x}, {y}, {z})");
                    }

                    ushort surface = world.GetBlock(x, top, z);
                    if (surface == Blocks.Grass) Assert.AreEqual(Blocks.Dirt, world.GetBlock(x, top - 1, z), $"Under the grass at ({x}, {z})");
                    else Assert.AreEqual(Blocks.Stone, surface, $"Surface at ({x}, {z})");
                }
            }
        }

        [Test]
        public void SteepSlopes_AreBareRock_AndGentleLowGround_IsGrass()
        {
            World world = CreateWorld();
            int grass = 0;
            int rock = 0;

            for (int z = 1; z < world.SizeZ - 1; z++)
            {
                for (int x = 1; x < world.SizeX - 1; x++)
                {
                    int height = TerrainGenerator.SurfaceHeight(x, z, Seed);
                    bool steep = System.Math.Abs(TerrainGenerator.SurfaceHeight(x + 1, z, Seed) - height) >= 3
                        || System.Math.Abs(TerrainGenerator.SurfaceHeight(x - 1, z, Seed) - height) >= 3
                        || System.Math.Abs(TerrainGenerator.SurfaceHeight(x, z + 1, Seed) - height) >= 3
                        || System.Math.Abs(TerrainGenerator.SurfaceHeight(x, z - 1, Seed) - height) >= 3;

                    ushort surface = world.GetBlock(x, height, z);
                    if (steep) Assert.AreEqual(Blocks.Stone, surface, $"Steep slope at ({x}, {z})");
                    if (surface == Blocks.Grass) grass++;
                    else rock++;
                }
            }

            Assert.That(grass, Is.GreaterThan(rock), "grass dominates: plants have room to grow");
        }

        [Test]
        public void AChunkGeneratedAlone_MatchesTheSameChunkOfAWholeWorld()
        {
            World world = CreateWorld();

            foreach (ChunkCoord coord in new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(3, 1, 2), new ChunkCoord(7, 2, 7), new ChunkCoord(5, 3, 0) })
            {
                var alone = new Chunk(coord);
                new TerrainGenerator().Generate(alone, Seed);
                Chunk inWorld = world.GetChunk(coord);

                for (int y = 0; y < Chunk.Size; y++)
                {
                    for (int z = 0; z < Chunk.Size; z++)
                    {
                        for (int x = 0; x < Chunk.Size; x++)
                        {
                            Assert.AreEqual(inWorld.Get(x, y, z), alone.Get(x, y, z), $"Chunk {coord}, block ({x}, {y}, {z})");
                        }
                    }
                }
            }
        }

        [Test]
        public void OneGenerator_ServesSeveralSeeds_WithoutMixingThemUp()
        {
            var shared = new TerrainGenerator();
            var first = new Chunk(new ChunkCoord(2, 1, 2));
            var other = new Chunk(new ChunkCoord(2, 1, 2));
            var again = new Chunk(new ChunkCoord(2, 1, 2));

            shared.Generate(first, 1);
            shared.Generate(other, 2);
            shared.Generate(again, 1);

            bool differs = false;
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        Assert.AreEqual(first.Get(x, y, z), again.Get(x, y, z));
                        if (first.Get(x, y, z) != other.Get(x, y, z)) differs = true;
                    }
                }
            }

            Assert.IsTrue(differs);
        }

        private static World CreateWorld()
        {
            var world = new World(8, 4, 8);
            world.Generate(new TerrainGenerator(), Seed);
            return world;
        }
    }

    public class IntNoiseTests
    {
        [Test]
        public void Sample_StaysInRange_IncludingAtNegativeCoordinates()
        {
            for (int z = -300; z < 300; z += 7)
            {
                for (int x = -300; x < 300; x += 7)
                {
                    Assert.That(IntNoise.Sample(x, z, 5, 99u), Is.GreaterThanOrEqualTo(0).And.LessThan(IntNoise.One));
                    Assert.That(IntNoise.Fractal(x, z, 6, 4, 99u), Is.GreaterThanOrEqualTo(0).And.LessThan(IntNoise.One));
                }
            }
        }

        [Test]
        public void Sample_IsSmooth_NeighbouringBlocksAreClose()
        {
            // On 16-block cells, the steepest slope of the smoothing curve gives less than one eighth per block.
            for (int x = -200; x < 200; x++)
            {
                int step = System.Math.Abs(IntNoise.Sample(x + 1, 37, 4, 5u) - IntNoise.Sample(x, 37, 4, 5u));
                Assert.That(step, Is.LessThan(IntNoise.One / 8), $"Between x = {x} and x = {x + 1}");
            }
        }

        [Test]
        public void Sample_DependsOnTheSeed_AndNotOnAnythingElse()
        {
            Assert.AreEqual(IntNoise.Sample(123, -45, 5, 7u), IntNoise.Sample(123, -45, 5, 7u));

            int differences = 0;
            for (int x = 0; x < 64; x++)
            {
                if (IntNoise.Sample(x * 9, 11, 5, 7u) != IntNoise.Sample(x * 9, 11, 5, 8u)) differences++;
            }

            Assert.That(differences, Is.GreaterThan(48));
        }
    }
}
