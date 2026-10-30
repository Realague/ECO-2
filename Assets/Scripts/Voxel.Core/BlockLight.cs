using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // The light blocks give off (torches, Blocks.LightOf), from 0 to Blocks.TorchLight, beside the
    // sky's (SkyLight). Display only, like the sky light: each machine that draws the world works it
    // out from its own blocks, nothing is sent or saved.
    //
    // The rules: a block that gives light has that level in its cell; every step to a neighbouring
    // cell that is not opaque loses one level; opaque blocks hold none. Most of the world is dark,
    // so only the chunks some light reaches keep an array of levels.
    //
    // Changes are worked out the way SkyLight does: the light that came through the changed cell is
    // taken away, then flows back from the cells around that kept theirs and from the sources.
    public sealed class BlockLight
    {
        private static readonly int[] StepX = { 1, -1, 0, 0, 0, 0 };
        private static readonly int[] StepY = { 0, 0, 1, -1, 0, 0 };
        private static readonly int[] StepZ = { 0, 0, 0, 0, 1, -1 };

        private readonly World world;
        private readonly int sizeX;
        private readonly int sizeZ;
        private readonly byte[][] levels;

        // Every cell that gives light, packed (Pack), for the torches' drawing.
        private readonly HashSet<int> sources = new HashSet<int>();

        private readonly Queue<int> spread = new Queue<int>();
        private readonly Queue<long> darken = new Queue<long>();
        private ICollection<Chunk> changedChunks;

        public BlockLight(World world)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            sizeX = world.SizeX;
            sizeZ = world.SizeZ;
            levels = new byte[world.Chunks.Count][];

            // Torches are never generated: they can only be in chunks players changed.
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.IsModified && !chunk.IsEmpty) AddSources(chunk);
            }

            Spread();
        }

        public int Count => sources.Count;

        public int Get(int x, int y, int z)
        {
            if (!world.InBounds(x, y, z)) return 0;
            return Level(x, y, z);
        }

        // The cells that give light (where torches stand).
        public IEnumerable<BlockPos> Sources
        {
            get
            {
                foreach (int packed in sources)
                {
                    Unpack(packed, out int x, out int y, out int z);
                    yield return new BlockPos(x, y, z);
                }
            }
        }

        // To call when the block at this position changed (World.BlockChanged). The chunks whose
        // meshes show a level that changed are added to changed.
        public void Update(BlockPos pos, ICollection<Chunk> changed)
        {
            if (!world.InBounds(pos.X, pos.Y, pos.Z)) return;

            changedChunks = changed;
            int packed = Pack(pos.X, pos.Y, pos.Z);
            sources.Remove(packed);
            Darken(pos.X, pos.Y, pos.Z);
            Relight();
            Seed(pos.X, pos.Y, pos.Z);
            Spread();
            changedChunks = null;
        }

        // To call when the blocks of a chunk were replaced wholesale, without World.SetBlock (a
        // client does so for each modified chunk the server sends).
        public void UpdateChunk(Chunk chunk, ICollection<Chunk> changed)
        {
            changedChunks = changed;
            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        sources.Remove(Pack(originX + x, originY + y, originZ + z));
                        if (Level(originX + x, originY + y, originZ + z) > 0) Darken(originX + x, originY + y, originZ + z);
                    }
                }
            }

            Relight();
            AddSources(chunk);
            Spread();
            changedChunks = null;
        }

        private void AddSources(Chunk chunk)
        {
            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;
            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        if (Blocks.LightOf(chunk.Get(x, y, z)) > 0) Seed(originX + x, originY + y, originZ + z);
                    }
                }
            }
        }

        // A cell whose block gives light lights itself, and the light will spread from it.
        private void Seed(int x, int y, int z)
        {
            int light = Blocks.LightOf(world.GetBlock(x, y, z));
            if (light <= 0) return;

            sources.Add(Pack(x, y, z));
            if (light > Level(x, y, z)) SetLevel(x, y, z, light);
            spread.Enqueue(Pack(x, y, z));
        }

        private void Darken(int x, int y, int z)
        {
            int level = Level(x, y, z);
            SetLevel(x, y, z, 0);
            darken.Enqueue(((long)Pack(x, y, z) << 4) | (uint)level);
        }

        // Spreads the darkness from the darkened cells: a neighbour dimmer than the cell it came from
        // was lit through it and goes dark too; a brighter or equal one has light of its own and will
        // shine back. A source taken in the darkness lights itself again.
        private void Relight()
        {
            while (darken.Count > 0)
            {
                long entry = darken.Dequeue();
                int level = (int)(entry & 0xF);
                Unpack((int)(entry >> 4), out int x, out int y, out int z);
                if (sources.Contains(Pack(x, y, z))) Seed(x, y, z);

                for (int direction = 0; direction < 6; direction++)
                {
                    int nx = x + StepX[direction];
                    int ny = y + StepY[direction];
                    int nz = z + StepZ[direction];
                    if (!world.InBounds(nx, ny, nz)) continue;

                    int neighbour = Level(nx, ny, nz);
                    if (neighbour == 0) continue;

                    if (neighbour < level)
                    {
                        SetLevel(nx, ny, nz, 0);
                        darken.Enqueue(((long)Pack(nx, ny, nz) << 4) | (uint)neighbour);
                    }
                    else
                    {
                        spread.Enqueue(Pack(nx, ny, nz));
                    }
                }
            }
        }

        // Lets light flow from the queued cells to their neighbours until nothing brightens.
        private void Spread()
        {
            while (spread.Count > 0)
            {
                Unpack(spread.Dequeue(), out int x, out int y, out int z);
                int level = Level(x, y, z);
                if (level <= 1) continue;

                for (int direction = 0; direction < 6; direction++)
                {
                    int nx = x + StepX[direction];
                    int ny = y + StepY[direction];
                    int nz = z + StepZ[direction];
                    if (!world.InBounds(nx, ny, nz)) continue;
                    if (Blocks.BlocksLight(world.GetBlock(nx, ny, nz))) continue;
                    if (level - 1 <= Level(nx, ny, nz)) continue;

                    SetLevel(nx, ny, nz, level - 1);
                    spread.Enqueue(Pack(nx, ny, nz));
                }
            }
        }

        private int Level(int x, int y, int z)
        {
            byte[] data = levels[ChunkIndex(x >> Chunk.SizeBits, y >> Chunk.SizeBits, z >> Chunk.SizeBits)];
            return data == null ? 0 : data[LocalIndex(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)];
        }

        private void SetLevel(int x, int y, int z, int level)
        {
            int chunk = ChunkIndex(x >> Chunk.SizeBits, y >> Chunk.SizeBits, z >> Chunk.SizeBits);
            int local = LocalIndex(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
            byte[] data = levels[chunk];
            if (data == null)
            {
                if (level == 0) return;

                data = new byte[Chunk.Volume];
                levels[chunk] = data;
            }
            else if (data[local] == level)
            {
                return;
            }

            data[local] = (byte)level;
            if (changedChunks != null) MarkChanged(x, y, z);
        }

        // A face shows the levels of the cells in front of it and around its corners, so a level
        // changes the meshes of every chunk within one block of the cell.
        private void MarkChanged(int x, int y, int z)
        {
            for (int cy = Math.Max(0, (y - 1) >> Chunk.SizeBits); cy <= Math.Min(world.ChunksY - 1, (y + 1) >> Chunk.SizeBits); cy++)
            {
                for (int cz = Math.Max(0, (z - 1) >> Chunk.SizeBits); cz <= Math.Min(world.ChunksZ - 1, (z + 1) >> Chunk.SizeBits); cz++)
                {
                    for (int cx = Math.Max(0, (x - 1) >> Chunk.SizeBits); cx <= Math.Min(world.ChunksX - 1, (x + 1) >> Chunk.SizeBits); cx++)
                    {
                        changedChunks.Add(world.Chunks[ChunkIndex(cx, cy, cz)]);
                    }
                }
            }
        }

        private int Pack(int x, int y, int z) => x + sizeX * (z + sizeZ * y);

        private void Unpack(int packed, out int x, out int y, out int z)
        {
            x = packed % sizeX;
            int rest = packed / sizeX;
            z = rest % sizeZ;
            y = rest / sizeZ;
        }

        // Same order as World's chunk array.
        private int ChunkIndex(int cx, int cy, int cz) => cx + world.ChunksX * (cz + world.ChunksZ * cy);

        private static int LocalIndex(int x, int y, int z) => x | (z << Chunk.SizeBits) | (y << (Chunk.SizeBits * 2));
    }
}
