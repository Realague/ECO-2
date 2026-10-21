using System.Collections.Generic;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class BiomesTests
    {
        [Test]
        public void EveryColumnOfAGeneratedWorld_HasAKnownName_AndTheWestEdgeIsSea()
        {
            var world = new World(16, 8, 16);
            world.Generate(new TerrainGenerator(), 1);
            Landscape landscape = Landscape.For(world.Seed, world.SizeX, world.SizeZ);

            var seen = new HashSet<string>();
            for (int z = 0; z < world.SizeZ; z += 4)
            {
                for (int x = 0; x < world.SizeX; x += 4)
                {
                    string name = Biomes.NameAt(world, landscape, x, z);
                    Assert.Contains(name, Biomes.All, $"Column ({x}, {z})");
                    seen.Add(name);
                    if (landscape.IsMarsh(x, z)) Assert.AreEqual(Biomes.Marsh, name);
                }
            }

            Assert.AreEqual(Biomes.Sea, Biomes.NameAt(world, landscape, 0, world.SizeZ / 2), "a sea runs along the west edge");
            Assert.That(seen.Count, Is.GreaterThan(3), "several kinds of land: " + string.Join(", ", seen));
            Assert.AreEqual("", Biomes.NameAt(world, landscape, -1, 0));
        }

        [Test]
        public void SnowAndWaterPlacedByPlayers_NameTheColumn()
        {
            var world = new World(16, 8, 16);
            world.Generate(new TerrainGenerator(), 1);
            Landscape landscape = Landscape.For(world.Seed, world.SizeX, world.SizeZ);
            // A dry column outside any marsh.
            int x = world.SizeX / 2;
            int z = world.SizeZ / 2;
            while (landscape.IsMarsh(x, z) || landscape.WaterTop(x, z) >= 0) x++;
            int top = world.GetTopHeight(x, z);

            world.SetBlock(x, top, z, Blocks.Snow);
            Assert.AreEqual(Biomes.Snowfield, Biomes.NameAt(world, landscape, x, z));

            world.SetBlock(x, top + 1, z, Blocks.Water);
            Assert.AreEqual(Biomes.Lake, Biomes.NameAt(world, null, x, z), "without a landscape, water is a lake");
        }
    }
}
