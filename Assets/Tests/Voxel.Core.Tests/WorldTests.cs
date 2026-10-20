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
                        Assert.AreEqual(first.GetBlock(x, y, z), second.GetBlock(x, y, z), $"Block ({x}, {y}, {z})");
                    }
                }
            }
        }

        // GetTopHeight and GetSurfaceHeight skip empty chunks and read blocks directly: they must give
        // what a scan of every block gives, on generated terrain (water, caves) and after edits that
        // fill an empty chunk or empty a full one, and on chunks read from a save.
        [Test]
        public void ColumnHeights_MatchAScanOfEveryBlock()
        {
            var world = new World(2, 4, 2);
            world.Generate(new TerrainGenerator(), 3);
            AssertHeightsMatchScan(world);

            world.SetBlock(5, 60, 7, Blocks.Stone);
            world.SetBlock(9, 50, 3, Blocks.Water);
            for (int y = 0; y < world.SizeY; y++)
            {
                world.SetBlock(20, y, 20, Blocks.Air);
            }

            AssertHeightsMatchScan(world);
            world.SetBlock(5, 60, 7, Blocks.Air);
            AssertHeightsMatchScan(world);

            var stream = new System.IO.MemoryStream();
            WorldSave.Write(stream, world);
            World loaded = WorldSave.Read(new System.IO.MemoryStream(stream.ToArray()), new TerrainGenerator());
            AssertHeightsMatchScan(loaded);
        }

        // TerrainGenerator fills chunks on every core at once (IParallelChunkGenerator): the same
        // generator run one chunk after another must give every block the same.
        [Test]
        public void ParallelGeneration_GivesTheSameBlocksAsOneChunkAfterAnother()
        {
            var parallel = new World(4, 4, 4);
            parallel.Generate(new TerrainGenerator(), 5);
            var serial = new World(4, 4, 4);
            serial.Generate(new OneAtATime(new TerrainGenerator()), 5);

            for (int i = 0; i < parallel.Chunks.Count; i++)
            {
                Chunk a = parallel.Chunks[i];
                Chunk b = serial.Chunks[i];
                Assert.AreEqual(b.IsEmpty, a.IsEmpty, $"Chunk {a.Coord} emptiness");
                for (int y = 0; y < Chunk.Size; y++)
                {
                    for (int z = 0; z < Chunk.Size; z++)
                    {
                        for (int x = 0; x < Chunk.Size; x++)
                        {
                            if (a.Get(x, y, z) != b.Get(x, y, z)) Assert.Fail($"Chunk {a.Coord}, block ({x}, {y}, {z})");
                        }
                    }
                }
            }

            Assert.AreEqual(serial.TerrainFingerprint, parallel.TerrainFingerprint);
        }

        // Hides that a generator may run in parallel, so World.Generate calls it chunk by chunk.
        private sealed class OneAtATime : IChunkGenerator
        {
            private readonly IChunkGenerator inner;

            public OneAtATime(IChunkGenerator inner)
            {
                this.inner = inner;
            }

            public int Version => inner.Version;

            public void Prepare(int seed, int sizeX, int sizeZ) => inner.Prepare(seed, sizeX, sizeZ);

            public void Generate(Chunk chunk, int seed) => inner.Generate(chunk, seed);
        }

        private static void AssertHeightsMatchScan(World world)
        {
            for (int z = -1; z <= world.SizeZ; z++)
            {
                for (int x = -1; x <= world.SizeX; x++)
                {
                    int top = 0;
                    int surface = 0;
                    for (int y = world.SizeY - 1; y >= 0; y--)
                    {
                        ushort block = world.GetBlock(x, y, z);
                        if (top == 0 && Blocks.TypeOf(block) != Blocks.Air) top = y + 1;
                        if (surface == 0 && Blocks.IsSolid(block)) surface = y + 1;
                    }

                    Assert.AreEqual(top, world.GetTopHeight(x, z), $"Top of ({x}, {z})");
                    Assert.AreEqual(surface, world.GetSurfaceHeight(x, z), $"Surface of ({x}, {z})");
                }
            }
        }

        [Test]
        public void TerrainFingerprint_IsTheSameForTheSameTerrain_AndDiffersForAnother()
        {
            Assert.AreEqual(CreateHills(seed: 42).TerrainFingerprint, CreateHills(seed: 42).TerrainFingerprint);
            Assert.AreNotEqual(CreateHills(seed: 42).TerrainFingerprint, CreateHills(seed: 43).TerrainFingerprint);
        }

        [Test]
        public void TerrainFingerprint_DescribesTheGeneratedTerrain_NotLaterEdits()
        {
            World world = CreateHills(seed: 42);
            ulong generated = world.TerrainFingerprint;

            for (int x = 0; x < world.SizeX; x++)
            {
                world.SetBlock(x, world.GetSurfaceHeight(x, 0), 0, Blocks.Stone);
            }

            Assert.AreEqual(generated, world.TerrainFingerprint);
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
            world.Generate(new TerrainGenerator(), seed);
            return world;
        }
    }
}
