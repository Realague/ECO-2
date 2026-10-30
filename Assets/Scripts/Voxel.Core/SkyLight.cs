using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // How much of the sky each block of a world sees, from 0 (none) to Max (open sky). Display only:
    // each machine that draws the world works it out from its own blocks, nothing is sent or saved.
    //
    // The rules: an air block with only air above it up to the top of the world gets Max, so open
    // sky falls straight down a shaft without fading; every other step, sideways, upwards, into or
    // through water, loses one level. Opaque blocks hold no light: the light of a face is that of the
    // block in front of it.
    //
    // Levels are kept per chunk; a chunk whose blocks all have the same level (open sky, or deep in
    // the rock) keeps that one value instead of an array.
    public sealed class SkyLight
    {
        public const int Max = 15;

        private static readonly int[] StepX = { 1, -1, 0, 0, 0, 0 };
        private static readonly int[] StepY = { 0, 0, 1, -1, 0, 0 };
        private static readonly int[] StepZ = { 0, 0, 0, 0, 1, -1 };
        private const int Down = 3;

        private readonly World world;
        private readonly int sizeX;
        private readonly int sizeY;
        private readonly int sizeZ;
        private readonly byte[][] levels;
        private readonly byte[] uniform;

        // Work queues, kept between updates. Cells are packed as x + sizeX * (z + sizeZ * y); the
        // darkening queue adds the cell's former level in the low 4 bits.
        private readonly Queue<int> spread = new Queue<int>();
        private readonly Queue<long> darken = new Queue<long>();
        private readonly List<int> darkenedTop = new List<int>();
        private ICollection<Chunk> changedChunks;

        public SkyLight(World world)
        {
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            sizeX = world.SizeX;
            sizeY = world.SizeY;
            sizeZ = world.SizeZ;
            levels = new byte[world.Chunks.Count][];
            uniform = new byte[world.Chunks.Count];

            ComputeAll();
        }

        // Above the world it is open sky; on its sides and below, rock.
        public int Get(int x, int y, int z)
        {
            if (y >= sizeY) return Max;
            if (!world.InBounds(x, y, z)) return 0;
            return Level(x, y, z);
        }

        // To call when the block at this position changed (World.BlockChanged). The chunks whose
        // meshes show a level that changed are added to changed.
        public void Update(BlockPos pos, ICollection<Chunk> changed)
        {
            if (!world.InBounds(pos.X, pos.Y, pos.Z)) return;

            changedChunks = changed;
            Darken(pos.X, pos.Y, pos.Z);
            Relight();
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
                        Darken(originX + x, originY + y, originZ + z);
                    }
                }
            }

            Relight();
            changedChunks = null;
        }

        // Starts darkening from a cell that changed: whatever light came through it is taken away.
        private void Darken(int x, int y, int z)
        {
            int index = Pack(x, y, z);
            int level = Level(x, y, z);
            SetLevel(x, y, z, 0);
            darken.Enqueue(((long)index << 4) | (uint)level);

            // Its neighbours that keep their light will relight it, if it lets light through now.
            if (y == sizeY - 1) darkenedTop.Add(index);
        }

        // Spreads the darkness, collecting the cells that kept their light, then lets light flow back
        // from them.
        private void Relight()
        {
            while (darken.Count > 0)
            {
                long entry = darken.Dequeue();
                int level = (int)(entry & 0xF);
                Unpack((int)(entry >> 4), out int x, out int y, out int z);

                for (int direction = 0; direction < 6; direction++)
                {
                    int nx = x + StepX[direction];
                    int ny = y + StepY[direction];
                    int nz = z + StepZ[direction];
                    if (!world.InBounds(nx, ny, nz)) continue;

                    int neighbour = Level(nx, ny, nz);
                    if (neighbour == 0)
                    {
                        // A dark neighbour that lets light through may need relighting from elsewhere:
                        // nothing to take away, but it is not a source either.
                        continue;
                    }

                    bool litByThis = neighbour < level || (direction == Down && level == Max && neighbour == Max);
                    if (litByThis)
                    {
                        SetLevel(nx, ny, nz, 0);
                        int packed = Pack(nx, ny, nz);
                        darken.Enqueue(((long)packed << 4) | (uint)neighbour);
                        if (ny == sizeY - 1) darkenedTop.Add(packed);
                    }
                    else
                    {
                        spread.Enqueue(Pack(nx, ny, nz));
                    }
                }
            }

            // The top layer is lit from above the world.
            foreach (int packed in darkenedTop)
            {
                Unpack(packed, out int x, out int y, out int z);
                int fromSky = FromAboveTheWorld(world.GetBlock(x, y, z));
                if (fromSky > Level(x, y, z))
                {
                    SetLevel(x, y, z, fromSky);
                    spread.Enqueue(packed);
                }
            }

            darkenedTop.Clear();
            Spread();
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

                    ushort block = world.GetBlock(nx, ny, nz);
                    if (Blocks.BlocksLight(block)) continue;

                    bool straightDown = direction == Down && level == Max && Blocks.TypeOf(block) == Blocks.Air;
                    int lit = straightDown ? Max : level - 1;
                    if (lit <= Level(nx, ny, nz)) continue;

                    SetLevel(nx, ny, nz, lit);
                    spread.Enqueue(Pack(nx, ny, nz));
                }
            }
        }

        private static int FromAboveTheWorld(ushort block)
        {
            if (Blocks.BlocksLight(block)) return 0;
            return Blocks.TypeOf(block) == Blocks.Air ? Max : Max - 1;
        }

        // The whole world at once: each column is lit from above (open sky down to the first block
        // that is not air, then one level less per block, down to the first opaque one), then light
        // spreads sideways from the columns' edges and from water.
        private void ComputeAll()
        {
            var skyTop = new int[sizeX * sizeZ];
            var solidTop = new int[sizeX * sizeZ];
            ScanColumns(skyTop, solidTop);

            for (int cy = 0; cy < world.ChunksY; cy++)
            {
                for (int cz = 0; cz < world.ChunksZ; cz++)
                {
                    for (int cx = 0; cx < world.ChunksX; cx++)
                    {
                        FillChunk(cx, cy, cz, skyTop, solidTop);
                    }
                }
            }

            // Light spreads from open sky into the columns beside it that are darker at the same
            // height, and from every cell of water or under water.
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int column = x + sizeX * z;
                    int top = skyTop[column];
                    int highestNeighbour = top;
                    if (x > 0) highestNeighbour = Math.Max(highestNeighbour, skyTop[column - 1]);
                    if (x < sizeX - 1) highestNeighbour = Math.Max(highestNeighbour, skyTop[column + 1]);
                    if (z > 0) highestNeighbour = Math.Max(highestNeighbour, skyTop[column - sizeX]);
                    if (z < sizeZ - 1) highestNeighbour = Math.Max(highestNeighbour, skyTop[column + sizeX]);

                    for (int y = solidTop[column] + 1; y < highestNeighbour; y++)
                    {
                        spread.Enqueue(Pack(x, y, z));
                    }
                }
            }

            Spread();

            for (int i = 0; i < levels.Length; i++)
            {
                Compact(i);
            }
        }

        // For each column: skyTop, the lowest height of open sky (Max from there up), and solidTop,
        // the height of the highest opaque block (-1 if none). Reads the chunks directly, top down.
        private void ScanColumns(int[] skyTop, int[] solidTop)
        {
            for (int cz = 0; cz < world.ChunksZ; cz++)
            {
                for (int cx = 0; cx < world.ChunksX; cx++)
                {
                    for (int lz = 0; lz < Chunk.Size; lz++)
                    {
                        for (int lx = 0; lx < Chunk.Size; lx++)
                        {
                            int column = cx * Chunk.Size + lx + sizeX * (cz * Chunk.Size + lz);
                            int sky = -1;
                            int solid = -1;
                            for (int cy = world.ChunksY - 1; cy >= 0 && solid < 0; cy--)
                            {
                                Chunk chunk = world.GetChunk(new ChunkCoord(cx, cy, cz));
                                for (int ly = Chunk.Size - 1; ly >= 0; ly--)
                                {
                                    ushort block = chunk.Get(lx, ly, lz);
                                    int y = cy * Chunk.Size + ly;
                                    if (sky < 0 && Blocks.TypeOf(block) != Blocks.Air) sky = y + 1;
                                    if (Blocks.BlocksLight(block))
                                    {
                                        solid = y;
                                        break;
                                    }
                                }
                            }

                            skyTop[column] = sky < 0 ? 0 : sky;
                            solidTop[column] = solid;
                        }
                    }
                }
            }
        }

        private void FillChunk(int cx, int cy, int cz, int[] skyTop, int[] solidTop)
        {
            int chunk = ChunkIndex(cx, cy, cz);
            int bottom = cy * Chunk.Size;
            int top = bottom + Chunk.Size - 1;

            int highestSky = int.MinValue;
            int lowestSolid = int.MaxValue;
            for (int lz = 0; lz < Chunk.Size; lz++)
            {
                for (int lx = 0; lx < Chunk.Size; lx++)
                {
                    int column = cx * Chunk.Size + lx + sizeX * (cz * Chunk.Size + lz);
                    highestSky = Math.Max(highestSky, skyTop[column]);
                    lowestSolid = Math.Min(lowestSolid, solidTop[column]);
                }
            }

            if (bottom >= highestSky)
            {
                uniform[chunk] = Max;
                return;
            }

            if (top <= lowestSolid)
            {
                uniform[chunk] = 0;
                return;
            }

            var data = new byte[Chunk.Volume];
            for (int lz = 0; lz < Chunk.Size; lz++)
            {
                for (int lx = 0; lx < Chunk.Size; lx++)
                {
                    int column = cx * Chunk.Size + lx + sizeX * (cz * Chunk.Size + lz);
                    int sky = skyTop[column];
                    int solid = solidTop[column];
                    for (int ly = 0; ly < Chunk.Size; ly++)
                    {
                        int y = bottom + ly;
                        int level = y >= sky ? Max : y <= solid ? 0 : Math.Max(0, Max - (sky - y));
                        data[LocalIndex(lx, ly, lz)] = (byte)level;
                    }
                }
            }

            levels[chunk] = data;
        }

        // A chunk whose cells all have the same level goes back to a single value.
        private void Compact(int chunk)
        {
            byte[] data = levels[chunk];
            if (data == null) return;

            byte first = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                if (data[i] != first) return;
            }

            levels[chunk] = null;
            uniform[chunk] = first;
        }

        private int Level(int x, int y, int z)
        {
            int chunk = ChunkIndex(x >> Chunk.SizeBits, y >> Chunk.SizeBits, z >> Chunk.SizeBits);
            byte[] data = levels[chunk];
            return data == null ? uniform[chunk] : data[LocalIndex(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask)];
        }

        private void SetLevel(int x, int y, int z, int level)
        {
            int chunk = ChunkIndex(x >> Chunk.SizeBits, y >> Chunk.SizeBits, z >> Chunk.SizeBits);
            int local = LocalIndex(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask);
            byte[] data = levels[chunk];
            if (data == null)
            {
                if (uniform[chunk] == level) return;

                data = new byte[Chunk.Volume];
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = uniform[chunk];
                }

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
