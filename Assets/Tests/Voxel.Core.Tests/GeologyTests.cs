using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class GeologyTests
    {
        private const int Seed = 1;

        private static World world;
        private static long[] counts;

        // A 512 × 128 × 512 world, generated once for all these tests.
        private static World Reference
        {
            get
            {
                if (world != null) return world;

                world = new World(32, 8, 32);
                world.Generate(new TerrainGenerator(), Seed);
                counts = new long[Blocks.LastType + 1];
                foreach (Chunk chunk in world.Chunks)
                {
                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        for (int z = 0; z < Chunk.Size; z++)
                        {
                            for (int x = 0; x < Chunk.Size; x++)
                            {
                                counts[Blocks.TypeOf(chunk.Get(x, y, z))]++;
                            }
                        }
                    }
                }

                return world;
            }
        }

        private static long Count(ushort type)
        {
            World unused = Reference;
            return counts[type];
        }

        [TestCase(Blocks.Granite)]
        [TestCase(Blocks.Basalt)]
        [TestCase(Blocks.Limestone)]
        [TestCase(Blocks.Marble)]
        [TestCase(Blocks.Sandstone)]
        [TestCase(Blocks.Slate)]
        [TestCase(Blocks.Sand)]
        [TestCase(Blocks.Snow)]
        [TestCase(Blocks.Gravel)]
        [TestCase(Blocks.Clay)]
        [TestCase(Blocks.Mud)]
        [TestCase(Blocks.CoalOre)]
        [TestCase(Blocks.IronOre)]
        [TestCase(Blocks.CopperOre)]
        [TestCase(Blocks.TinOre)]
        [TestCase(Blocks.GoldOre)]
        [TestCase(Blocks.RockSalt)]
        [TestCase(Blocks.Flint)]
        public void EachRockGroundAndOre_IsFound(ushort type)
        {
            Assert.That(Count(type), Is.GreaterThan(500), Items.NameOf(type));
        }

        [Test]
        public void Ores_AreRarerThanTheirRocks_AndGoldRarerThanIron()
        {
            long rock = Count(Blocks.Granite) + Count(Blocks.Limestone) + Count(Blocks.Sandstone) + Count(Blocks.Slate) + Count(Blocks.Basalt);
            long ores = Count(Blocks.CoalOre) + Count(Blocks.IronOre) + Count(Blocks.CopperOre) + Count(Blocks.TinOre) + Count(Blocks.GoldOre);
            Assert.That(ores * 20, Is.LessThan(rock), "ores are under one block in twenty");
            Assert.That(Count(Blocks.GoldOre) * 4, Is.LessThan(Count(Blocks.IronOre)));
        }

        [Test]
        public void TheBottom_IsBedrock_AndNothingElseIsBedrock()
        {
            World reference = Reference;
            for (int z = 0; z < reference.SizeZ; z += 7)
            {
                for (int x = 0; x < reference.SizeX; x += 7)
                {
                    Assert.AreEqual(Blocks.Bedrock, reference.GetBlock(x, 0, z));
                    for (int y = 3; y < reference.SizeY; y++)
                    {
                        Assert.AreNotEqual(Blocks.Bedrock, reference.GetBlock(x, y, z), $"({x}, {y}, {z})");
                    }
                }
            }
        }

        // Tin is only found in the granite of cold regions, gold only deep down, marble only at
        // the base of limestone.
        [Test]
        public void Resources_AreWhereTheyBelong()
        {
            World reference = Reference;
            Landscape landscape = Landscape.For(Seed, reference.SizeX, reference.SizeZ);
            for (int z = 0; z < reference.SizeZ; z += 3)
            {
                for (int x = 0; x < reference.SizeX; x += 3)
                {
                    Landscape.ColumnGeology geology = landscape.Column(x, z);
                    int ground = landscape.Ground(x, z);
                    for (int y = 1; y <= ground; y++)
                    {
                        ushort block = reference.GetBlock(x, y, z);
                        if (block == Blocks.TinOre) Assert.IsTrue(geology.Cold, $"Tin at ({x}, {y}, {z})");
                        if (block == Blocks.GoldOre) Assert.That(ground - y, Is.GreaterThanOrEqualTo(25), $"Gold at ({x}, {y}, {z})");
                        if (block == Blocks.Marble)
                        {
                            Assert.AreEqual(Blocks.Limestone, geology.Cover, $"Marble at ({x}, {y}, {z})");
                            Assert.That(y, Is.InRange(geology.CoverBottom, geology.CoverBottom + 1), $"Marble at ({x}, {y}, {z})");
                        }
                    }
                }
            }
        }

        [Test]
        public void ABlockCanBeBroken_ExceptBedrock()
        {
            Assert.IsTrue(Blocks.IsBreakable(Blocks.Granite));
            Assert.IsTrue(Blocks.IsBreakable(Blocks.GoldOre));
            Assert.IsFalse(Blocks.IsBreakable(Blocks.Bedrock));
            Assert.IsFalse(Blocks.IsBreakable(Blocks.Water));
        }
    }
}
