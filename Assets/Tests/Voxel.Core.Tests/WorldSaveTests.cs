using System;
using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class WorldSaveTests
    {
        private const int HeaderSize = 32;

        private string temporaryDirectory;

        [SetUp]
        public void CreateTemporaryDirectory()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "VoxelCoreTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
        }

        [TearDown]
        public void DeleteTemporaryDirectory()
        {
            Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void WriteThenRead_RestoresWorldIncludingModifiedBlocks()
        {
            World original = CreateHills(seed: 7);
            original.SetBlock(3, original.GetSurfaceHeight(3, 4) - 1, 4, Blocks.Air);
            original.SetBlock(20, 62, 30, Blocks.WithOrientation(Blocks.Stone, 5));
            original.SetBlock(16, 40, 0, Blocks.Dirt);

            World loaded = RoundTrip(original);

            Assert.AreEqual(7, loaded.Seed);
            Assert.AreEqual(original.GeneratorVersion, loaded.GeneratorVersion);
            Assert.AreEqual(original.ChunksX, loaded.ChunksX);
            Assert.AreEqual(original.ChunksY, loaded.ChunksY);
            Assert.AreEqual(original.ChunksZ, loaded.ChunksZ);
            AssertSameBlocks(original, loaded);
        }

        [Test]
        public void WriteThenRead_KeepsTrackOfWhichChunksWereModified()
        {
            World original = CreateHills(seed: 7);
            original.SetBlock(20, 62, 30, Blocks.Stone);

            World loaded = RoundTrip(original);

            for (int i = 0; i < original.Chunks.Count; i++)
            {
                Assert.AreEqual(original.Chunks[i].IsModified, loaded.Chunks[i].IsModified, $"Chunk {original.Chunks[i].Coord}");
            }

            Assert.IsTrue(loaded.GetChunk(ChunkCoord.FromBlock(new BlockPos(20, 62, 30))).IsModified);
        }

        [Test]
        public void Write_UnmodifiedWorld_StoresOnlyTheHeader()
        {
            World world = CreateHills(seed: 7);

            var stream = new MemoryStream();
            WorldSave.Write(stream, world);

            Assert.AreEqual(HeaderSize, stream.Length);
        }

        [Test]
        public void Read_SaveFromAnotherGeneratorVersion_Throws()
        {
            var world = new World(1, 1, 1);
            world.Generate(new FlatGenerator { Version = 1 }, 0);
            var stream = new MemoryStream();
            WorldSave.Write(stream, world);
            stream.Position = 0;

            Assert.Throws<InvalidDataException>(() => WorldSave.Read(stream, new FlatGenerator { Version = 2 }));
        }

        [Test]
        public void Read_FileThatIsNotASave_Throws()
        {
            var stream = new MemoryStream(new byte[64]);

            Assert.Throws<InvalidDataException>(() => WorldSave.Read(stream, new FlatGenerator()));
        }

        [Test]
        public void Read_TruncatedSave_Throws()
        {
            World world = FlatGenerator.CreateWorld();
            world.SetBlock(3, 3, 3, Blocks.Air);
            var stream = new MemoryStream();
            WorldSave.Write(stream, world);
            byte[] data = stream.ToArray();
            var truncated = new MemoryStream(data, 0, data.Length - 1);

            Assert.Throws<EndOfStreamException>(() => WorldSave.Read(truncated, new FlatGenerator()));
        }

        [Test]
        public void SaveToFile_ThenLoadFromFile_RestoresWorld()
        {
            string path = Path.Combine(temporaryDirectory, "world.dat");
            World original = CreateHills(seed: 7);
            original.SetBlock(20, 62, 30, Blocks.Stone);

            WorldSave.SaveToFile(path, original);
            World loaded = WorldSave.LoadFromFile(path, new TerrainGenerator());

            AssertSameBlocks(original, loaded);
        }

        [Test]
        public void SaveToFile_OverExistingSave_ReplacesItAndLeavesNoTemporaryFile()
        {
            string path = Path.Combine(temporaryDirectory, "world.dat");
            World world = CreateHills(seed: 7);
            WorldSave.SaveToFile(path, world);
            world.SetBlock(20, 62, 30, Blocks.Stone);

            WorldSave.SaveToFile(path, world);
            World loaded = WorldSave.LoadFromFile(path, new TerrainGenerator());

            Assert.AreEqual(Blocks.Stone, loaded.GetBlock(20, 62, 30));
            Assert.AreEqual(new[] { path }, Directory.GetFiles(temporaryDirectory));
        }

        private static World CreateHills(int seed)
        {
            var world = new World(2, 4, 2);
            world.Generate(new TerrainGenerator(), seed);
            return world;
        }

        private static World RoundTrip(World world)
        {
            var stream = new MemoryStream();
            WorldSave.Write(stream, world);
            stream.Position = 0;
            return WorldSave.Read(stream, new TerrainGenerator());
        }

        private static void AssertSameBlocks(World expected, World actual)
        {
            for (int y = 0; y < expected.SizeY; y++)
            {
                for (int z = 0; z < expected.SizeZ; z++)
                {
                    for (int x = 0; x < expected.SizeX; x++)
                    {
                        Assert.AreEqual(expected.GetBlock(x, y, z), actual.GetBlock(x, y, z), $"Block ({x}, {y}, {z})");
                    }
                }
            }
        }
    }
}
