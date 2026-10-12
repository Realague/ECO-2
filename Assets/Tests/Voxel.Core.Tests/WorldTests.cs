using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class WorldTests
    {
        [Test]
        public void Generate_SameSeed_ProducesSameBlocks()
        {
            World first = CreateHills(seed: 42);
            World second = CreateHills(seed: 42);

            for (int y = 0; y < first.SizeY; y++)
            {
                for (int z = 0; z < first.SizeZ; z++)
                {
                    for (int x = 0; x < first.SizeX; x++)
                    {
                        Assert.AreEqual(first.GetBlock(x, y, z), second.GetBlock(x, y, z), $"Bloc ({x}, {y}, {z})");
                    }
                }
            }
        }

        [Test]
        public void Generate_Hills_HasGrassSurfaceInsideWorld()
        {
            World world = CreateHills(seed: 42);

            int surface = world.GetSurfaceHeight(5, 9);

            Assert.That(surface, Is.GreaterThan(0).And.LessThan(world.SizeY));
            Assert.AreEqual(Blocks.Grass, world.GetBlock(5, surface - 1, 9));
            Assert.AreEqual(Blocks.Air, world.GetBlock(5, surface, 9));
        }

        [Test]
        public void SetBlock_ChangesBlock_MarksChunkAndRaisesEvent()
        {
            World world = FlatGenerator.CreateWorld();
            var target = new BlockPos(3, FlatGenerator.GroundHeight - 1, 5);
            int raised = 0;
            BlockPos reportedPos = default;
            ushort reportedPrevious = 0;
            ushort reportedCurrent = 0;
            world.BlockChanged += (pos, previous, current) =>
            {
                raised++;
                reportedPos = pos;
                reportedPrevious = previous;
                reportedCurrent = current;
            };

            bool changed = world.SetBlock(target, Blocks.Air);

            Assert.IsTrue(changed);
            Assert.AreEqual(Blocks.Air, world.GetBlock(target));
            Assert.IsTrue(world.GetChunk(ChunkCoord.FromBlock(target)).IsModified);
            Assert.AreEqual(1, raised);
            Assert.AreEqual(target, reportedPos);
            Assert.AreEqual(Blocks.Stone, reportedPrevious);
            Assert.AreEqual(Blocks.Air, reportedCurrent);
        }

        [Test]
        public void SetBlock_SameBlockOrOutOfBounds_DoesNothing()
        {
            World world = FlatGenerator.CreateWorld();
            int raised = 0;
            world.BlockChanged += (pos, previous, current) => raised++;

            Assert.IsFalse(world.SetBlock(3, 0, 5, Blocks.Stone));
            Assert.IsFalse(world.SetBlock(-1, 0, 0, Blocks.Stone));
            Assert.IsFalse(world.SetBlock(0, world.SizeY, 0, Blocks.Stone));

            Assert.AreEqual(0, raised);
            Assert.IsFalse(world.GetChunk(new ChunkCoord(0, 0, 0)).IsModified);
        }

        [Test]
        public void IsSolid_OutsideWorld_SidesAndBottomAreSolidTopIsAir()
        {
            World world = FlatGenerator.CreateWorld();

            Assert.IsTrue(world.IsSolid(-1, 12, 4));
            Assert.IsTrue(world.IsSolid(4, 12, world.SizeZ));
            Assert.IsTrue(world.IsSolid(4, -1, 4));
            Assert.IsFalse(world.IsSolid(4, world.SizeY, 4));
            Assert.IsFalse(world.IsSolid(4, 12, 4));
        }

        private static World CreateHills(int seed)
        {
            var world = new World(2, 4, 2);
            world.Generate(new ProvisionalHillsGenerator(), seed);
            return world;
        }
    }
}
