using System;
using System.IO;

namespace Voxel.Core
{
    // Blocks of a chunk compressed in runs: (length, block) repeated until the chunk is covered.
    // Used for saving, and as is for sending chunks over the network.
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

                // A run covers at most a whole chunk (4096 blocks): it fits in two bytes.
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
                    throw new InvalidDataException($"Corrupted data for chunk {chunk.Coord}.");
                }

                // Blocks of earlier versions that no longer exist (crafted blocks) are gone.
                if (Blocks.IsRetired(block)) block = Blocks.Air;

                Array.Fill(blocks, block, index, run);
                index += run;
            }

            chunk.RecountBlocks();
        }
    }
}
