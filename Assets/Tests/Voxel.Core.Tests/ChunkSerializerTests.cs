using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ChunkSerializerTests
    {
        private static readonly ushort[] Types = { Blocks.Stone, Blocks.Dirt, Blocks.Grass, Blocks.Water, Blocks.Basalt };

        // Planks (4) and the other crafted blocks no longer exist: a save holding them reads them as air.
        [Test]
        public void Read_TurnsRemovedBlocksIntoAir()
        {
            var original = new Chunk(new ChunkCoord(0, 0, 0));
            original.Set(1, 2, 3, 4);
            original.Set(4, 5, 6, 31);
            original.Set(7, 8, 9, Blocks.Stone);

            Chunk copy = RoundTrip(original);

            Assert.AreEqual(Blocks.Air, copy.Get(1, 2, 3));
            Assert.AreEqual(Blocks.Air, copy.Get(4, 5, 6));
            Assert.AreEqual(Blocks.Stone, copy.Get(7, 8, 9));
        }

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
                        ushort type = Types[(x * 7 + y * 13 + z / 3) % Types.Length];
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
                        Assert.AreEqual(original.Get(x, y, z), copy.Get(x, y, z), $"Block ({x}, {y}, {z})");
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
