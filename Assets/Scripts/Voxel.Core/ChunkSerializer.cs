using System;
using System.IO;

namespace Voxel.Core
{
    // Blocs d'un chunk compressés par plages : (longueur, bloc) répété jusqu'à couvrir le chunk.
    // Sert à la sauvegarde et servira tel quel à l'envoi des chunks sur le réseau.
    public static class ChunkSerializer
    {
        public static void Write(BinaryWriter writer, Chunk chunk)
        {
            ushort[] blocks = chunk.RawBlocks;
            int index = 0;
            while (index < blocks.Length)
            {
                ushort block = blocks[index];
                int run = 1;
                while (index + run < blocks.Length && blocks[index + run] == block)
                {
                    run++;
                }

                // Une plage couvre au plus un chunk entier (4096 blocs) : elle tient sur deux octets.
                writer.Write((ushort)run);
                writer.Write(block);
                index += run;
            }
        }

        public static void Read(BinaryReader reader, Chunk chunk)
        {
            ushort[] blocks = chunk.RawBlocks;
            int index = 0;
            while (index < blocks.Length)
            {
                int run = reader.ReadUInt16();
                ushort block = reader.ReadUInt16();
                if (run == 0 || index + run > blocks.Length)
                {
                    throw new InvalidDataException($"Données corrompues pour le chunk {chunk.Coord}.");
                }

                Array.Fill(blocks, block, index, run);
                index += run;
            }
        }
    }
}
