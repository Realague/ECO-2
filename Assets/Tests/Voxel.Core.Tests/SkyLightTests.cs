using System.Collections.Generic;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class SkyLightTests
    {
        private const int Ground = FlatGenerator.GroundHeight;

        // 32 × 32 × 32 blocks of flat ground, solid below height 8.
        private static World CreateWorld()
        {
            var world = new World(2, 2, 2);
            world.Generate(new FlatGenerator(), 0);
            return world;
        }

        private static void Set(World world, SkyLight light, int x, int y, int z, ushort block)
        {
            world.SetBlock(x, y, z, block);
            light.Update(new BlockPos(x, y, z), new HashSet<Chunk>());
        }

        [Test]
        public void OpenGround_IsLitAboveAndDarkBelow()
        {
            var light = new SkyLight(CreateWorld());

            Assert.AreEqual(SkyLight.Max, light.Get(5, Ground, 5));
            Assert.AreEqual(SkyLight.Max, light.Get(5, 31, 5));
            Assert.AreEqual(0, light.Get(5, Ground - 1, 5));
            Assert.AreEqual(SkyLight.Max, light.Get(5, 40, 5), "Above the world");
            Assert.AreEqual(0, light.Get(-1, 20, 5), "Beside the world");
        }

        [Test]
        public void Shaft_GetsOpenSkyAllTheWayDown()
        {
            World world = CreateWorld();
            var light = new SkyLight(world);

            for (int y = Ground - 1; y >= 3; y--)
            {
                Set(world, light, 5, y, 5, Blocks.Air);
            }

            Assert.AreEqual(SkyLight.Max, light.Get(5, 3, 5));
        }

        [Test]
        public void SideTunnel_LosesOneLevelPerBlock()
        {
            World world = CreateWorld();
            var light = new SkyLight(world);
            for (int y = Ground - 1; y >= 3; y--)
            {
                Set(world, light, 5, y, 5, Blocks.Air);
            }

            for (int x = 6; x <= 12; x++)
            {
                Set(world, light, x, 3, 5, Blocks.Air);
            }

            Assert.AreEqual(SkyLight.Max - 1, light.Get(6, 3, 5));
            Assert.AreEqual(SkyLight.Max - 7, light.Get(12, 3, 5));
        }

        [Test]
        public void UnderARoof_LightComesFromTheSides()
        {
            World world = CreateWorld();
            var light = new SkyLight(world);

            // A roof over x and z 10 to 16: the centre is four blocks from open sky.
            for (int z = 10; z <= 16; z++)
            {
                for (int x = 10; x <= 16; x++)
                {
                    Set(world, light, x, 12, z, Blocks.Stone);
                }
            }

            Assert.AreEqual(SkyLight.Max - 4, light.Get(13, 11, 13));
            Assert.AreEqual(SkyLight.Max - 4, light.Get(13, Ground, 13));
            Assert.AreEqual(SkyLight.Max - 1, light.Get(10, Ground, 13));
            Assert.AreEqual(SkyLight.Max, light.Get(13, 13, 13), "On the roof");
        }

        [Test]
        public void Water_LosesOneLevelPerBlock()
        {
            World world = CreateWorld();
            for (int z = 10; z <= 14; z++)
            {
                for (int x = 10; x <= 14; x++)
                {
                    world.SetBlock(x, Ground - 1, z, Blocks.Water);
                    world.SetBlock(x, Ground - 2, z, Blocks.Water);
                }
            }

            var light = new SkyLight(world);

            Assert.AreEqual(SkyLight.Max - 1, light.Get(12, Ground - 1, 12));
            Assert.AreEqual(SkyLight.Max - 2, light.Get(12, Ground - 2, 12));
        }

        // Whatever the edits, updating as they happen gives what working out the whole world again gives.
        [Test]
        public void Updates_GiveTheSameLevelsAsComputingAgain()
        {
            World world = CreateWorld();
            var light = new SkyLight(world);
            ushort[] choices = { Blocks.Air, Blocks.Air, Blocks.Stone, Blocks.Stone, Blocks.Water };
            uint state = 12345;

            for (int i = 0; i < 600; i++)
            {
                int x = (int)(Next(ref state) % 12) + 4;
                int y = (int)(Next(ref state) % 16) + 2;
                int z = (int)(Next(ref state) % 12) + 4;
                Set(world, light, x, y, z, choices[Next(ref state) % (uint)choices.Length]);
            }

            AssertSameAsComputedAgain(world, light);
        }

        [Test]
        public void UpdateChunk_GivesTheSameLevelsAsComputingAgain()
        {
            World world = CreateWorld();
            var light = new SkyLight(world);

            // Raw writes, as when a chunk is received: a roof over the whole chunk and a hole in the ground.
            Chunk chunk = world.GetChunk(new ChunkCoord(0, 0, 0));
            for (int z = 0; z < Chunk.Size; z++)
            {
                for (int x = 0; x < Chunk.Size; x++)
                {
                    chunk.Set(x, 14, z, Blocks.Stone);
                }
            }

            chunk.Set(3, Ground - 1, 3, Blocks.Air);
            chunk.Set(3, Ground - 2, 3, Blocks.Water);
            light.UpdateChunk(chunk, new HashSet<Chunk>());

            AssertSameAsComputedAgain(world, light);
        }

        [Test]
        public void Update_ReportsTheChunksWhoseLightChanged()
        {
            World world = CreateWorld();
            var light = new SkyLight(world);
            var changed = new HashSet<Chunk>();

            // A block at the corner of chunk (0, 1, 0) shades the cells below it, down to the ground in chunk (0, 0, 0).
            world.SetBlock(15, 20, 15, Blocks.Stone);
            light.Update(new BlockPos(15, 20, 15), changed);

            Assert.IsTrue(changed.Contains(world.GetChunk(new ChunkCoord(0, 0, 0))));
            Assert.IsTrue(changed.Contains(world.GetChunk(new ChunkCoord(1, 1, 1))), "Neighbouring chunk, within one block");
        }

        private static void AssertSameAsComputedAgain(World world, SkyLight light)
        {
            var expected = new SkyLight(world);
            for (int y = 0; y < world.SizeY; y++)
            {
                for (int z = 0; z < world.SizeZ; z++)
                {
                    for (int x = 0; x < world.SizeX; x++)
                    {
                        Assert.AreEqual(expected.Get(x, y, z), light.Get(x, y, z), $"Level at ({x}, {y}, {z})");
                    }
                }
            }
        }

        private static uint Next(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return state >> 8;
        }
    }
}
