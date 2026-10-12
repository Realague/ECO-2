using System.IO;
using System.Text;

namespace Voxel.Core
{
    // Sauvegarde d'un monde : un en-tête (graine, version du générateur, dimensions) suivi des seuls
    // chunks modifiés. Les chunks intacts sont régénérés au chargement.
    public static class WorldSave
    {
        private const uint Magic = 0x574F4345; // « ECOW »
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

        // Lève InvalidDataException si le fichier n'est pas une sauvegarde valide pour ce générateur,
        // et EndOfStreamException s'il est tronqué.
        public static World Read(Stream stream, IChunkGenerator generator)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic)
                {
                    throw new InvalidDataException("Ce fichier n'est pas une sauvegarde de monde.");
                }

                int formatVersion = reader.ReadInt32();
                if (formatVersion != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"Format de sauvegarde {formatVersion} non pris en charge (attendu : {FormatVersion}).");
                }

                int seed = reader.ReadInt32();
                int generatorVersion = reader.ReadInt32();
                if (generatorVersion != generator.Version)
                {
                    throw new InvalidDataException(
                        $"Sauvegarde créée avec la version {generatorVersion} du générateur, incompatible avec la version {generator.Version}.");
                }

                int chunksX = reader.ReadInt32();
                int chunksY = reader.ReadInt32();
                int chunksZ = reader.ReadInt32();
                if (chunksX <= 0 || chunksY <= 0 || chunksZ <= 0)
                {
                    throw new InvalidDataException("Dimensions du monde invalides dans la sauvegarde.");
                }

                var world = new World(chunksX, chunksY, chunksZ);
                world.Generate(generator, seed);

                int modifiedCount = reader.ReadInt32();
                for (int i = 0; i < modifiedCount; i++)
                {
                    var coord = new ChunkCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    Chunk chunk = world.GetChunk(coord);
                    if (chunk == null)
                    {
                        throw new InvalidDataException($"La sauvegarde contient un chunk hors du monde : {coord}.");
                    }

                    ChunkSerializer.Read(reader, chunk);
                    chunk.IsModified = true;
                }

                return world;
            }
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
