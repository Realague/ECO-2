using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // Batch of chunks sent from the server to a client: for each chunk, its coordinates then its blocks.
    public static class ChunkBatch
    {
        // Writes chunks starting at index start while the stream stays under maxBytes, and at least
        // one. Returns the number of chunks written. The stream must be truncatable.
        public static int Write(BinaryWriter writer, IReadOnlyList<Chunk> chunks, int start, int maxBytes)
        {
            Stream stream = writer.BaseStream;
            int written = 0;

            while (start + written < chunks.Count)
            {
                Chunk chunk = chunks[start + written];
                long before = stream.Position;

                writer.Write(chunk.Coord.X);
                writer.Write(chunk.Coord.Y);
                writer.Write(chunk.Coord.Z);
                ChunkSerializer.Write(writer, chunk);
                writer.Flush();

                if (stream.Position > maxBytes && written > 0)
                {
                    stream.SetLength(before);
                    break;
                }

                written++;
            }

            return written;
        }

        // Replaces the blocks of the batch's chunks in the world and adds those chunks to received.
        public static void Read(BinaryReader reader, World world, List<Chunk> received)
        {
            Stream stream = reader.BaseStream;
            while (stream.Position < stream.Length)
            {
                var coord = new ChunkCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                Chunk chunk = world.GetChunk(coord);
                if (chunk == null)
                {
                    throw new InvalidDataException($"Received a chunk outside the world: {coord}.");
                }

                ChunkSerializer.Read(reader, chunk);
                received.Add(chunk);
            }
        }
    }
}
