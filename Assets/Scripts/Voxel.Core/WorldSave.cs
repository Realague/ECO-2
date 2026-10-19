using System.IO;
using System.Text;

namespace Voxel.Core
{
    // Save of a world: a header (seed, generator version, dimensions) followed by the modified
    // chunks only. Untouched chunks are regenerated on load.
    public static class WorldSave
    {
        private const uint Magic = 0x574F4345; // "ECOW"
        private const int FormatVersion = 1;

        public static void Write(Stream stream, World world)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(world.Seed);
                writer.Write(world.GeneratorVersion);
                writer.Write(world.ChunksX);
                writer.Write(world.ChunksY);
                writer.Write(world.ChunksZ);

                int modifiedCount = 0;
                foreach (Chunk chunk in world.Chunks)
                {
                    if (chunk.IsModified) modifiedCount++;
                }

                writer.Write(modifiedCount);
                foreach (Chunk chunk in world.Chunks)
                {
                    if (!chunk.IsModified) continue;

                    writer.Write(chunk.Coord.X);
                    writer.Write(chunk.Coord.Y);
                    writer.Write(chunk.Coord.Z);
                    ChunkSerializer.Write(writer, chunk);
                }
            }
        }

        // Throws InvalidDataException if the file is not a valid save for this generator,
        // and EndOfStreamException if it is truncated.
        public static World Read(Stream stream, IChunkGenerator generator)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                Header header = ReadHeader(reader);
                if (header.GeneratorVersion != generator.Version)
                {
                    throw new InvalidDataException(
                        $"Save created with generator version {header.GeneratorVersion}, incompatible with version {generator.Version}.");
                }

                var world = new World(header.ChunksX, header.ChunksY, header.ChunksZ);
                world.Generate(generator, header.Seed);

                int modifiedCount = reader.ReadInt32();
                for (int i = 0; i < modifiedCount; i++)
                {
                    var coord = new ChunkCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    Chunk chunk = world.GetChunk(coord);
                    if (chunk == null)
                    {
                        throw new InvalidDataException($"The save contains a chunk outside the world: {coord}.");
                    }

                    ChunkSerializer.Read(reader, chunk);
                    chunk.IsModified = true;
                }

                return world;
            }
        }

        // What a save says about its world, read without generating it (for a list of saves).
        public struct Header
        {
            public int Seed;
            public int GeneratorVersion;
            public int ChunksX;
            public int ChunksY;
            public int ChunksZ;
        }

        // Throws InvalidDataException if the file is not a world save, and EndOfStreamException if it is truncated.
        public static Header ReadHeaderFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                return ReadHeader(reader);
            }
        }

        private static Header ReadHeader(BinaryReader reader)
        {
            if (reader.ReadUInt32() != Magic)
            {
                throw new InvalidDataException("This file is not a world save.");
            }

            int formatVersion = reader.ReadInt32();
            if (formatVersion != FormatVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported save format {formatVersion} (expected: {FormatVersion}).");
            }

            var header = new Header
            {
                Seed = reader.ReadInt32(),
                GeneratorVersion = reader.ReadInt32(),
                ChunksX = reader.ReadInt32(),
                ChunksY = reader.ReadInt32(),
                ChunksZ = reader.ReadInt32()
            };

            if (header.ChunksX <= 0 || header.ChunksY <= 0 || header.ChunksZ <= 0)
            {
                throw new InvalidDataException("Invalid world dimensions in the save.");
            }

            return header;
        }

        public static void SaveToFile(string path, World world)
        {
            AtomicFile.Write(path, stream => Write(stream, world));
        }

        public static World LoadFromFile(string path, IChunkGenerator generator)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream, generator);
            }
        }
    }
}
