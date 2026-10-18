using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class TerrainGeneratorTests
    {
        private const int Seed = 1;

        // Relief recorded with version 3 of the generator, the whole world with version 5 (version 6
        // added marshes, which this small world has none of: LandscapeTests pins them). If this
        // test fails, the produced terrain has changed: existing saves no longer match and clients on
        // another version would generate another world. Bump TerrainGenerator.Version, then update
        // these values.
        [TestCase(0, 0, 1, 35)]
        [TestCase(64, 64, 1, 41)]
        [TestCase(500, -300, 1, 25)]
        [TestCase(-1000, 777, 1, 28)]
        [TestCase(12345, 6789, 1, 44)]
        [TestCase(100, 900, 1, 42)]
        [TestCase(64, 64, 42, 23)]
        public void ReliefHeight_IsUnchanged(int x, int z, int seed, int expected)
        {
            Assert.AreEqual(expected, TerrainGenerator.ReliefHeight(x, z, seed));
        }

        [Test]
        public void GeneratedWorld_IsUnchangedSinceThisGeneratorVersion()
        {
            Assert.AreEqual(6, new TerrainGenerator().Version, "reference value to update for the new version");
            Assert.AreEqual(8051785332826314626UL, CreateWorld().TerrainFingerprint);
        }

        [Test]
        public void DifferentSeeds_GiveDifferentTerrain()
        {
            int differences = 0;
            for (int x = 0; x < 256; x += 4)
            {
                if (TerrainGenerator.ReliefHeight(x, 100, 1) != TerrainGenerator.ReliefHeight(x, 100, 2)) differences++;
            }

            Assert.That(differences, Is.GreaterThan(16));
        }

        [Test]
        public void Relief_HasFlatLandAndHighGround_AndFitsInASixtyFourBlockHighWorld()
        {
            int lowest = int.MaxValue;
            int highest = int.MinValue;
            for (int z = -512; z < 512; z += 3)
            {
                for (int x = -512; x < 512; x += 3)
                {
                    int height = TerrainGenerator.ReliefHeight(x, z, Seed);
                    if (height < lowest) lowest = height;
                    if (height > highest) highest = height;
                }
            }

            Assert.That(lowest, Is.GreaterThanOrEqualTo(20), "the plains' ground is at sea level");
            Assert.That(highest, Is.LessThan(62), "the relief fits under the world's ceiling");
            Assert.That(highest - lowest, Is.GreaterThan(20), "there are plains and massifs");
        }

        // Each column: bedrock at the bottom, solid up to the landscape's ground (except in caves: air,
        // or water in cave lakes), then water up to its water top (ice on top where it is cold), then
        // air. The top block and the three under it are those the landscape chose (grass on dirt,
        // sand, snow; the bed under water); bare rock has rock up to its top, or snow on it.
        [Test]
        public void EveryColumn_IsGroundThenWaterThenAir()
        {
            World world = CreateWorld();
            Landscape landscape = Landscape.For(Seed, world.SizeX, world.SizeZ);

            for (int z = 0; z < world.SizeZ; z++)
            {
                for (int x = 0; x < world.SizeX; x++)
                {
                    int ground = landscape.Ground(x, z);
                    int water = landscape.WaterTop(x, z);
                    Landscape.ColumnGeology geology = landscape.Column(x, z);
                    Assert.AreEqual(Blocks.Bedrock, world.GetBlock(x, 0, z), $"Bottom of ({x}, {z})");

                    for (int y = 0; y <= ground; y++)
                    {
                        ushort block = world.GetBlock(x, y, z);
                        if (landscape.Caves.IsHollow(x, y, z))
                        {
                            ushort hollow = landscape.Caves.IsLake(x, y, z) ? Blocks.Water : Blocks.Air;
                            Assert.AreEqual(hollow, block, $"Cave at ({x}, {y}, {z})");
                        }
                        else
                        {
                            Assert.IsTrue(Blocks.IsSolid(block), $"Hole at ({x}, {y}, {z})");
                        }
                    }

                    // Where a cave opens at the surface, the top blocks are gone.
                    if (landscape.Caves.IsHollow(x, ground, z) || landscape.Caves.IsHollow(x, ground - 1, z)) continue;

                    // Ice is solid: on a frozen lake, it is the surface.
                    int surfaceHeight = landscape.IsIce(x, z) ? water + 1 : ground + 1;
                    Assert.AreEqual(surfaceHeight, world.GetSurfaceHeight(x, z), $"Column ({x}, {z})");

                    for (int y = ground + 1; y <= water; y++)
                    {
                        ushort expected = y == water && landscape.IsIce(x, z) ? Blocks.Ice : Blocks.Water;
                        Assert.AreEqual(expected, world.GetBlock(x, y, z), $"Water at ({x}, {y}, {z})");
                    }

                    Assert.AreEqual(Blocks.Air, world.GetBlock(x, System.Math.Max(ground, water) + 1, z), $"Air above ({x}, {z})");

                    ushort surface = world.GetBlock(x, ground, z);
                    if (landscape.IsRock(x, z) && water <= ground)
                    {
                        Assert.IsTrue(surface == Blocks.Snow || IsRock(surface), $"Bare rock at ({x}, {z}): {surface}");
                    }
                    else
                    {
                        Assert.AreEqual(geology.Top, surface, $"Top of ({x}, {z})");
                        Assert.AreEqual(geology.Under, world.GetBlock(x, ground - 1, z), $"Under the top of ({x}, {z})");
                    }
                }
            }
        }

        private static bool IsRock(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type == Blocks.Granite || (type >= Blocks.Basalt && type <= Blocks.Slate) || (type >= Blocks.CoalOre && type <= Blocks.Flint);
        }

        [Test]
        public void SteepDrySlopes_AreBareRock_AndGentleLowGround_IsMostlyGrass()
        {
            World world = CreateWorld();
            Landscape landscape = Landscape.For(Seed, world.SizeX, world.SizeZ);
            int grass = 0;
            int rock = 0;

            for (int z = 1; z < world.SizeZ - 1; z++)
            {
                for (int x = 1; x < world.SizeX - 1; x++)
                {
                    if (landscape.WaterTop(x, z) >= 0) continue;

                    int height = landscape.Ground(x, z);
                    if (landscape.Caves.IsHollow(x, height, z)) continue;

                    bool steep = System.Math.Abs(landscape.Ground(x + 1, z) - height) >= 3
                        || System.Math.Abs(landscape.Ground(x - 1, z) - height) >= 3
                        || System.Math.Abs(landscape.Ground(x, z + 1) - height) >= 3
                        || System.Math.Abs(landscape.Ground(x, z - 1) - height) >= 3;

                    ushort surface = world.GetBlock(x, height, z);
                    if (steep) Assert.IsTrue(surface == Blocks.Snow || IsRock(surface), $"Steep slope at ({x}, {z}): {surface}");
                    if (surface == Blocks.Grass) grass++;
                    else if (IsRock(surface)) rock++;
                }
            }

            Assert.That(grass, Is.GreaterThan(rock * 2), "grass dominates bare rock: plants have room to grow");
        }

        [Test]
        public void AChunkGeneratedAlone_MatchesTheSameChunkOfAWholeWorld()
        {
            World world = CreateWorld();
            var generator = new TerrainGenerator();
            generator.Prepare(Seed, world.SizeX, world.SizeZ);

            foreach (ChunkCoord coord in new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(3, 1, 2), new ChunkCoord(7, 2, 7), new ChunkCoord(5, 3, 0) })
            {
                var alone = new Chunk(coord);
                generator.Generate(alone, Seed);
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

            shared.Prepare(1, 128, 128);
            shared.Generate(first, 1);
            shared.Prepare(2, 128, 128);
            shared.Generate(other, 2);
            shared.Prepare(1, 128, 128);
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
