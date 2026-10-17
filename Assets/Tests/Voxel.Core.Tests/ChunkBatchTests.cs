using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ChunkBatchTests
    {
        [Test]
        public void WritingAllChunksInSmallBatches_ThenReading_RebuildsTheWorld()
        {
            World source = CreateHills();
            source.SetBlock(20, 62, 30, Blocks.Stone);
            var target = new World(source.ChunksX, source.ChunksY, source.ChunksZ);
            var received = new List<Chunk>();

            int sent = 0;
            int batches = 0;
            while (sent < source.Chunks.Count)
            {
                byte[] batch = WriteBatch(source.Chunks, sent, 64, out int written);
                Assert.That(written, Is.GreaterThan(0));
                sent += written;
                batches++;

                using (var reader = new BinaryReader(new MemoryStream(batch)))
                {
                    ChunkBatch.Read(reader, target, received);
                }
            }

            Assert.That(batches, Is.GreaterThan(1));
            Assert.AreEqual(source.Chunks.Count, received.Count);
            for (int y = 0; y < source.SizeY; y++)
            {
                for (int z = 0; z < source.SizeZ; z++)
                {
                    for (int x = 0; x < source.SizeX; x++)
                    {
                        Assert.AreEqual(source.GetBlock(x, y, z), target.GetBlock(x, y, z), $"Block ({x}, {y}, {z})");
                    }
                }
            }
        }

        [Test]
        public void Write_StaysUnderTheByteBudget_WhenMoreThanOneChunkIsWritten()
        {
            World source = CreateHills();

            // From the first chunk of sky (the third layer of chunks): empty chunks are small, so several fit.
            int firstSky = 2 * source.ChunksX * source.ChunksZ;
            byte[] batch = WriteBatch(source.Chunks, firstSky, 64, out int written);

            Assert.That(written, Is.GreaterThan(1));
            Assert.That(batch.Length, Is.LessThanOrEqualTo(64));
        }

        [Test]
        public void Write_AlwaysWritesAtLeastOneChunk_EvenOverBudget()
        {
            World source = CreateHills();

            byte[] batch = WriteBatch(source.Chunks, 0, 1, out int written);

            Assert.AreEqual(1, written);
            Assert.That(batch.Length, Is.GreaterThan(1));
        }

        [Test]
        public void Write_FromTheEnd_WritesNothing()
        {
            World source = CreateHills();

            byte[] batch = WriteBatch(source.Chunks, source.Chunks.Count, 1024, out int written);

            Assert.AreEqual(0, written);
            Assert.AreEqual(0, batch.Length);
        }

        [Test]
        public void Read_ChunkOutsideTheWorld_Throws()
        {
            World source = CreateHills();
            byte[] batch = WriteBatch(source.Chunks, source.Chunks.Count - 1, 1024, out _);
            var smallerWorld = new World(1, 1, 1);

            using (var reader = new BinaryReader(new MemoryStream(batch)))
            {
                Assert.Throws<InvalidDataException>(() => ChunkBatch.Read(reader, smallerWorld, new List<Chunk>()));
            }
        }

        private static World CreateHills()
        {
            var world = new World(2, 4, 2);
            world.Generate(new TerrainGenerator(), 7);
            return world;
        }

        private static byte[] WriteBatch(IReadOnlyList<Chunk> chunks, int start, int maxBytes, out int written)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream))
            {
                written = ChunkBatch.Write(writer, chunks, start, maxBytes);
            }

            return stream.ToArray();
        }
    }
}
