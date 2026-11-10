using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class GeologyTests
    {
        private const int Seed = 1;

        private static World world;
        private static long[] counts;
        private static long[] oreCounts;

        // A 512 × 128 × 512 world, generated once for all these tests.
        private static World Reference
        {
            get
            {
                if (world != null) return world;

                world = new World(32, 8, 32);
                world.Generate(new TerrainGenerator(), Seed);
                counts = new long[Blocks.LastType + 1];
                oreCounts = new long[Ores.Count + 1];
                foreach (Chunk chunk in world.Chunks)
                {
                    for (int y = 0; y < Chunk.Size; y++)
                    {
                        for (int z = 0; z < Chunk.Size; z++)
                        {
                            for (int x = 0; x < Chunk.Size; x++)
                            {
                                ushort block = chunk.Get(x, y, z);
                                counts[Blocks.TypeOf(block)]++;
                                oreCounts[(int)Ores.OreOf(block)]++;
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

        private static long Count(OreKind ore)
        {
            World unused = Reference;
            return oreCounts[(int)ore];
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
        public void EachRockAndGround_IsFound(ushort type)
        {
            Assert.That(Count(type), Is.GreaterThan(500), Items.NameOf(type));
        }

        [TestCase(OreKind.Coal)]
        [TestCase(OreKind.Iron)]
        [TestCase(OreKind.Copper)]
        [TestCase(OreKind.Tin)]
        [TestCase(OreKind.Gold)]
        [TestCase(OreKind.RockSalt)]
        [TestCase(OreKind.Flint)]
        public void EachOre_IsFound(OreKind ore)
        {
            Assert.That(Count(ore), Is.GreaterThan(500), Ores.NameOf(ore));
        }

        [Test]
        public void Ores_AreRarerThanTheirRocks_AndGoldRarerThanIron()
        {
            long rock = Count(Blocks.Granite) + Count(Blocks.Limestone) + Count(Blocks.Sandstone) + Count(Blocks.Slate) + Count(Blocks.Basalt);
            long ores = Count(OreKind.Coal) + Count(OreKind.Iron) + Count(OreKind.Copper) + Count(OreKind.Tin) + Count(OreKind.Gold);
            Assert.That(ores * 20, Is.LessThan(rock), "ores are in under one block of rock in twenty");
            Assert.That(Count(OreKind.Gold) * 4, Is.LessThan(Count(OreKind.Iron)));
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
                        OreKind ore = Ores.OreOf(block);
                        if (ore == OreKind.Tin) Assert.IsTrue(geology.Cold, $"Tin at ({x}, {y}, {z})");
                        if (ore == OreKind.Gold) Assert.That(ground - y, Is.GreaterThanOrEqualTo(25), $"Gold at ({x}, {y}, {z})");
                        if (Blocks.TypeOf(block) == Blocks.Marble)
                        {
                            Assert.AreEqual(Blocks.Limestone, geology.Cover, $"Marble at ({x}, {y}, {z})");
                            Assert.That(y, Is.InRange(geology.CoverBottom, geology.CoverBottom + 1), $"Marble at ({x}, {y}, {z})");
                        }
                    }
                }
            }
        }

        // An ore is held by the rock where it lies: the column's cover rock, or granite below it.
        // Flint only lies in limestone, rock salt in sandstone, and iron in every rock.
        [Test]
        public void Ores_AreHeldByTheRockTheyLieIn()
        {
            World reference = Reference;
            Landscape landscape = Landscape.For(Seed, reference.SizeX, reference.SizeZ);
            var ironRocks = new System.Collections.Generic.HashSet<ushort>();
            for (int z = 0; z < reference.SizeZ; z += 3)
            {
                for (int x = 0; x < reference.SizeX; x += 3)
                {
                    Landscape.ColumnGeology geology = landscape.Column(x, z);
                    for (int y = 3; y <= landscape.Ground(x, z); y++)
                    {
                        ushort block = reference.GetBlock(x, y, z);
                        OreKind ore = Ores.OreOf(block);
                        if (ore == OreKind.None) continue;

                        ushort rock = Blocks.TypeOf(block);
                        ushort expected = y < geology.CoverBottom ? Blocks.Granite : geology.Cover;
                        Assert.AreEqual(expected, rock, $"{Ores.NameOf(ore)} at ({x}, {y}, {z})");
                        if (ore == OreKind.Flint) Assert.AreEqual(Blocks.Limestone, rock);
                        if (ore == OreKind.RockSalt) Assert.AreEqual(Blocks.Sandstone, rock);
                        if (ore == OreKind.Iron) ironRocks.Add(rock);
                    }
                }
            }

            Assert.That(ironRocks.Count, Is.GreaterThanOrEqualTo(3), "iron lies in granite, basalt and the sedimentary rocks");
        }

        [Test]
        public void ARockHoldingAnOre_IsStillTheRock_AndGivesTheOreAsAnItem()
        {
            ushort block = Ores.Make(Blocks.Sandstone, OreKind.Iron);
            Assert.AreEqual(Blocks.Sandstone, Blocks.TypeOf(block));
            Assert.AreEqual(OreKind.Iron, Ores.OreOf(block));
            Assert.IsTrue(Blocks.IsValid(block));
            Assert.AreEqual((int)Blocks.Sandstone, Items.DropOf(block), "the block gives its rock");
            Assert.IsTrue(Items.IsOre(Items.Ore(OreKind.Iron)));
            Assert.IsFalse(Items.IsBlock(Items.Ore(OreKind.Iron)), "an ore is an item, not a block");
            Assert.AreEqual(OreKind.Iron, Items.OreKindOf(Items.Ore(OreKind.Iron)));
            Assert.AreEqual(1, Ores.Yield(OreKind.Iron));

            Assert.AreEqual(OreKind.None, Ores.OreOf(Blocks.Sandstone));
            Assert.AreEqual(OreKind.None, Ores.OreOf(Blocks.Dirt), "only rock holds ore");
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Granite, Ores.Count + 1, 0)));
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Granite, 1, 1)), "rocks are not oriented");
            Assert.IsFalse(Blocks.IsValid(Blocks.WithVariant(Blocks.Dirt, 1, 0)), "only rock and wood use the variant");
        }

        // The ores were blocks of their own (types 18 to 24, their host rock in their variant): a save's
        // block of those types is read as that rock holding the ore, an item as the ore.
        [Test]
        public void TheFormerOreBlocks_AreReadAsRockHoldingTheOre()
        {
            const ushort formerIron = 19;
            const ushort formerFlint = 24;
            Assert.AreEqual(Ores.Make(Blocks.Granite, OreKind.Iron), Ores.UpgradeBlock(formerIron), "saved without a host: granite");
            Assert.AreEqual(Ores.Make(Blocks.Sandstone, OreKind.Iron), Ores.UpgradeBlock(Blocks.WithVariant(formerIron, 4, 0)));
            Assert.AreEqual(Ores.Make(Blocks.Limestone, OreKind.Flint), Ores.UpgradeBlock(Blocks.WithVariant(formerFlint, 2, 0)));
            Assert.AreEqual(Blocks.Basalt, Ores.UpgradeBlock(Blocks.Basalt));
            Assert.IsFalse(Blocks.IsValid(formerIron));
            Assert.IsFalse(Items.IsBlock(formerIron));
            Assert.AreEqual(Items.Ore(OreKind.Iron), Items.Upgrade(formerIron));
            Assert.AreEqual(Items.Ore(OreKind.Flint), Items.Upgrade(formerFlint));

            var chunk = new Chunk(new ChunkCoord(0, 0, 0));
            chunk.Set(1, 2, 3, Blocks.WithVariant(formerIron, 4, 0));
            var stream = new System.IO.MemoryStream();
            ChunkSerializer.Write(new System.IO.BinaryWriter(stream), chunk);
            stream.Position = 0;
            var read = new Chunk(new ChunkCoord(0, 0, 0));
            ChunkSerializer.Read(new System.IO.BinaryReader(stream), read);
            Assert.AreEqual(Ores.Make(Blocks.Sandstone, OreKind.Iron), read.Get(1, 2, 3));
        }

        [Test]
        public void ABlockCanBeBroken_ExceptBedrock()
        {
            Assert.IsTrue(Blocks.IsBreakable(Blocks.Granite));
            Assert.IsTrue(Blocks.IsBreakable(Ores.Make(Blocks.Granite, OreKind.Gold)));
            Assert.IsFalse(Blocks.IsBreakable(Blocks.Bedrock));
            Assert.IsFalse(Blocks.IsBreakable(Blocks.Water));
        }
    }
}
