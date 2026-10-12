using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ChunkSerializerTests
    {
        [Test]
        public void WriteThenRead_PreservesEveryBlockAndOrientation()
        {
            var original = new Chunk(new ChunkCoord(0, 0, 0));
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        var type = (ushort)(1 + (x * 7 + y * 13 + z / 3) % 5);
                        original.Set(x, y, z, Blocks.WithOrientation(type, (x + z) % 16));
                    }
                }
            }

            Chunk copy = RoundTrip(original);

            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        Assert.AreEqual(original.Get(x, y, z), copy.Get(x, y, z), $"Bloc ({x}, {y}, {z})");
                    }
                }
            }
        }

        [Test]
        public void Write_UniformChunk_TakesASingleRun()
        {
            var chunk = new Chunk(new ChunkCoord(0, 0, 0));

            byte[] data = Serialize(chunk);

            Assert.AreEqual(4, data.Length);
        }

        [Test]
        public void Write_LayeredTerrain_IsFarSmallerThanRawBlocks()
        {
            World world = FlatGenerator.CreateWorld();

            byte[] data = Serialize(world.GetChunk(new ChunkCoord(0, 0, 0)));

            Assert.AreEqual(8, data.Length);
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)5000)]
        public void Read_InvalidRunLength_Throws(ushort runLength)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(runLength);
                writer.Write(Blocks.Stone);
            }

            stream.Position = 0;
            using (var reader = new BinaryReader(stream))
            {
                Assert.Throws<InvalidDataException>(
                    () => ChunkSerializer.Read(reader, new Chunk(new ChunkCoord(0, 0, 0))));
            }
        }

        [Test]
        public void Read_TruncatedData_Throws()
        {
            using (var reader = new BinaryReader(new MemoryStream(new byte[] { 16, 0, 1, 0 })))
            {
                Assert.Throws<EndOfStreamException>(
                    () => ChunkSerializer.Read(reader, new Chunk(new ChunkCoord(0, 0, 0))));
            }
        }

        private static byte[] Serialize(Chunk chunk)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream))
            {
                ChunkSerializer.Write(writer, chunk);
            }

            return stream.ToArray();
        }

        private static Chunk RoundTrip(Chunk chunk)
        {
            var copy = new Chunk(chunk.Coord);
            using (var reader = new BinaryReader(new MemoryStream(Serialize(chunk))))
            {
                ChunkSerializer.Read(reader, copy);
            }

            return copy;
        }
    }
}
