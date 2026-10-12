using System.Collections.Generic;
using System.IO;

namespace Voxel.Core
{
    // Lot de chunks envoyé du serveur à un client : pour chaque chunk, ses coordonnées puis ses blocs.
    public static class ChunkBatch
    {
        // Écrit des chunks à partir de l'indice start tant que le flux reste sous maxBytes, et au moins
        // un. Renvoie le nombre de chunks écrits. Le flux doit pouvoir être tronqué.
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

        // Remplace les blocs des chunks du lot dans le monde et ajoute ces chunks à received.
        public static void Read(BinaryReader reader, World world, List<Chunk> received)
        {
            Stream stream = reader.BaseStream;
            while (stream.Position < stream.Length)
            {
                var coord = new ChunkCoord(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                Chunk chunk = world.GetChunk(coord);
                if (chunk == null)
                {
                    throw new InvalidDataException($"Chunk reçu hors du monde : {coord}.");
                }

                ChunkSerializer.Read(reader, chunk);
                received.Add(chunk);
            }
        }
    }
}
