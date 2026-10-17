using System;
using System.Collections.Generic;

namespace Voxel.Core
{
    // Caves dug into the rock, laid out once for the whole world by Landscape and carved by
    // TerrainGenerator into each chunk it generates.
    //
    // Caves are made of hollows, rounded volumes (a sphere for a tunnel's section, a wide low dome
    // for a cavern), strung along tunnels that wander through the rock like worms: each step a
    // tunnel turns and tilts a little, and its width changes. The world is cut into regions of
    // 32 × 32 columns, and a region may hold a tunnel system, more often in limestone (karst) than
    // elsewhere; some systems open onto the surface, some tunnels fork, some widen into a cavern,
    // and some caverns hold a lake. Volcanic provinces have lava tubes: long, round, nearly level
    // galleries just under the basalt's surface.
    //
    // Rules that keep the world sound: nothing is carved at the bottom of the world (bedrock and
    // the block above it), nor under or beside surface water unless four blocks of rock stay above
    // (so lakes, rivers and the sea never stand over a void), and a cavern keeps four blocks of
    // rock over it. A cave lake is the water filling the lowest part of a cavern and every tunnel
    // joined to it below its level; a lake that would reach the open air or spread too far is not
    // made, so cave water is always held by rock.
    //
    // As for the terrain, everything is integer and done in a fixed order: every machine digs the
    // same caves. Positions are in 1/16 block.
    public sealed class Caves
    {
        private const int Sub = 16;
        private const int RegionShift = 5;

        // Blocks at or below this height are never carved.
        private const int LowestRock = 2;

        // Rock kept over a cave under or beside surface water (within WaterMargin columns), and over a cavern.
        private const int WaterRoof = 4;
        private const int WaterMargin = 2;
        private const int CavernRoof = 4;

        // Chances, in 1/1024, that a region holds a tunnel system, by the cover rock where it starts.
        private const int LimestoneChance = 700;
        private const int BasaltChance = 450;
        private const int OtherChance = 350;

        // Chances, in 1/1024: a system opening on the surface; a tunnel forking; a tunnel widening into a cavern.
        private const int EntranceChance = 300;
        private const int ForkChance = 600;
        private const int KarstCavernChance = 500;
        private const int OtherCavernChance = 200;

        // Chances, in 1/1024, that a cavern holds a lake.
        private const int KarstLakeChance = 550;
        private const int OtherLakeChance = 250;

        // A lake is looked for within this many blocks of its cavern's centre, and holds at most this many blocks.
        private const int LakeReach = 24;
        private const int MaxLakeBlocks = 4000;

        private const uint CaveSeedMask = 0x3C6EF372u;
        private const uint LakeSeedMask = 0xA54FF53Au;

        private readonly Landscape landscape;
        private readonly int sizeX;
        private readonly int sizeZ;
        private readonly int chunksX;
        private readonly List<Hollow> hollows = new List<Hollow>();
        private readonly List<int> caverns = new List<int>();

        // Per column, the highest block that may be carved (lower near surface water).
        private readonly short[] ceiling;

        // Per chunk column, the hollows that reach over it.
        private int[][] byColumn;

        // The blocks of cave lakes, packed as x + sizeX * (z + sizeZ * y).
        private readonly HashSet<long> lakeBlocks = new HashSet<long>();

        // For MarkHollows: the pass in which each hollow was last marked.
        private int[] marks;
        private int stamp;

        private struct Hollow
        {
            public int X;
            public int Y;
            public int Z;
            public int Across;
            public int Up;
            public int Down;
        }

        // A small generator of integers (xorshift), seeded from the position of what it decides.
        private struct Dice
        {
            private uint state;

            public Dice(uint seed)
            {
                state = seed == 0 ? 1u : seed;
            }

            // From min (included) to max (excluded).
            public int Next(int min, int max)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return min + (int)(state % (uint)(max - min));
            }
        }

        public Caves(int seed, Landscape landscape, int sizeX, int sizeZ)
        {
            this.landscape = landscape;
            this.sizeX = sizeX;
            this.sizeZ = sizeZ;
            chunksX = (sizeX + Chunk.Size - 1) / Chunk.Size;
            ceiling = new short[sizeX * sizeZ];

            uint caveSeed = unchecked((uint)seed);
            LayCeiling();
            DigSystems(caveSeed);
            IndexHollows();
            FillLakes(caveSeed);
            marks = null;
        }

        public int HollowCount => hollows.Count;
        public int CavernCount => caverns.Count;
        public int EntranceCount { get; private set; }
        public int LakeCount { get; private set; }

        // Whether generation leaves this block empty (air, or a cave lake's water).
        public bool IsHollow(int x, int y, int z)
        {
            if (!MayCarve(x, y, z)) return false;

            int px = x * Sub + Sub / 2;
            int py = y * Sub + Sub / 2;
            int pz = z * Sub + Sub / 2;
            foreach (int index in byColumn[(x >> Chunk.SizeBits) + chunksX * (z >> Chunk.SizeBits)])
            {
                if (Inside(hollows[index], px, py, pz)) return true;
            }

            return false;
        }

        public bool IsLake(int x, int y, int z) => lakeBlocks.Contains(Pack(x, y, z));

        // Empties the hollows inside a freshly generated chunk: air, or water in cave lakes.
        public void Carve(Chunk chunk)
        {
            int chunkColumn = chunk.Coord.X + chunksX * chunk.Coord.Z;
            if (chunkColumn < 0 || chunkColumn >= byColumn.Length) return;

            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;
            foreach (int index in byColumn[chunkColumn])
            {
                Hollow hollow = hollows[index];
                int minX = Math.Max(originX, (hollow.X - hollow.Across) >> 4);
                int maxX = Math.Min(originX + Chunk.Size - 1, (hollow.X + hollow.Across) >> 4);
                int minZ = Math.Max(originZ, (hollow.Z - hollow.Across) >> 4);
                int maxZ = Math.Min(originZ + Chunk.Size - 1, (hollow.Z + hollow.Across) >> 4);
                int minY = Math.Max(originY, (hollow.Y - hollow.Down) >> 4);
                int maxY = Math.Min(originY + Chunk.Size - 1, (hollow.Y + hollow.Up) >> 4);

                for (int y = minY; y <= maxY; y++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            if (!MayCarve(x, y, z)) continue;
                            if (!Inside(hollow, x * Sub + Sub / 2, y * Sub + Sub / 2, z * Sub + Sub / 2)) continue;

                            chunk.Set(x - originX, y - originY, z - originZ, IsLake(x, y, z) ? Blocks.Water : Blocks.Air);
                        }
                    }
                }
            }
        }

        private bool MayCarve(int x, int y, int z)
        {
            return (uint)x < (uint)sizeX && (uint)z < (uint)sizeZ && y > LowestRock && y <= ceiling[x + sizeX * z];
        }

        // Inside the hollow's rounded volume: its upper half reaches Up, its lower half Down.
        private static bool Inside(in Hollow hollow, int px, int py, int pz)
        {
            long dx = px - hollow.X;
            long dy = py - hollow.Y;
            long dz = pz - hollow.Z;
            long vertical = dy >= 0 ? hollow.Up : hollow.Down;
            long across = hollow.Across;
            return (dx * dx + dz * dz) * vertical * vertical + dy * dy * across * across <= across * across * vertical * vertical;
        }

        // Surface water keeps WaterRoof blocks of rock under it and beside it.
        private void LayCeiling()
        {
            for (int i = 0; i < ceiling.Length; i++)
            {
                ceiling[i] = short.MaxValue;
            }

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    if (landscape.WaterTop(x, z) < 0) continue;

                    var roof = (short)(landscape.Ground(x, z) - WaterRoof);
                    for (int dz = -WaterMargin; dz <= WaterMargin; dz++)
                    {
                        for (int dx = -WaterMargin; dx <= WaterMargin; dx++)
                        {
                            int nx = x + dx;
                            int nz = z + dz;
                            if ((uint)nx >= (uint)sizeX || (uint)nz >= (uint)sizeZ) continue;

                            int index = nx + sizeX * nz;
                            if (roof < ceiling[index]) ceiling[index] = roof;
                        }
                    }
                }
            }
        }

        private void DigSystems(uint seed)
        {
            int regionsX = (sizeX + (1 << RegionShift) - 1) >> RegionShift;
            int regionsZ = (sizeZ + (1 << RegionShift) - 1) >> RegionShift;
            for (int rz = 0; rz < regionsZ; rz++)
            {
                for (int rx = 0; rx < regionsX; rx++)
                {
                    var dice = new Dice(Hash(rx, rz, 0, seed ^ CaveSeedMask));
                    int x = Math.Min(sizeX - 1, (rx << RegionShift) + dice.Next(0, 1 << RegionShift));
                    int z = Math.Min(sizeZ - 1, (rz << RegionShift) + dice.Next(0, 1 << RegionShift));
                    ushort rock = landscape.Column(x, z).Cover;
                    int chance = rock == Blocks.Limestone ? LimestoneChance : rock == Blocks.Basalt ? BasaltChance : OtherChance;
                    if (dice.Next(0, 1024) >= chance) continue;

                    if (rock == Blocks.Basalt && dice.Next(0, 2) == 0) LavaTube(ref dice, x, z);
                    else TunnelSystem(ref dice, x, z, rock);
                }
            }
        }

        private void TunnelSystem(ref Dice dice, int x, int z, ushort rock)
        {
            int ground = landscape.Ground(x, z);
            bool dry = landscape.WaterTop(x, z) < 0;
            bool entrance = dry && dice.Next(0, 1024) < EntranceChance;
            int y;
            int pitch;
            if (entrance)
            {
                y = ground;
                pitch = -dice.Next(20, 40);
                EntranceCount++;
            }
            else
            {
                if (ground < LowestRock + 14) return;
                y = dice.Next(LowestRock + 5, ground - 6);
                pitch = dice.Next(-10, 11);
            }

            int cavernChance = rock == Blocks.Limestone ? KarstCavernChance : OtherCavernChance;
            Tunnel(ref dice, x * Sub + Sub / 2, y * Sub + Sub / 2, z * Sub + Sub / 2, dice.Next(0, 360), pitch,
                dice.Next(50, 160), 2, cavernChance, entrance);
        }

        // A wandering tunnel of hollows, one block apart. Away from its entrance it keeps under the
        // ground and above the bottom of the world, and it levels out as it goes.
        private void Tunnel(ref Dice dice, int x, int y, int z, int yaw, int pitch, int length, int forksLeft, int cavernChance, bool fromSurface)
        {
            int radius = dice.Next(22, 36);
            int turn = 0;
            int cavernAt = dice.Next(0, 1024) < cavernChance ? dice.Next(length / 4, length) : -1;
            int forkAt = forksLeft > 0 && dice.Next(0, 1024) < ForkChance ? dice.Next(length / 4, length * 3 / 4) : -1;

            for (int step = 0; step < length; step++)
            {
                int bx = x >> 4;
                int by = y >> 4;
                int bz = z >> 4;
                if ((uint)bx >= (uint)sizeX || (uint)bz >= (uint)sizeZ || by <= LowestRock + 1) return;

                bool leavingEntrance = fromSurface && step < 12;
                if (!leavingEntrance && by > landscape.Ground(bx, bz) - 3) pitch = Math.Min(pitch, -12);
                if (by < LowestRock + 4) pitch = Math.Max(pitch, 8);

                Add(x, y, z, radius, radius, radius * 3 / 4);
                if (step == cavernAt) Cavern(ref dice, x, y, z);
                if (step == forkAt)
                {
                    int side = dice.Next(0, 2) == 0 ? -1 : 1;
                    Tunnel(ref dice, x, y, z, yaw + side * dice.Next(40, 100), dice.Next(-15, 10), length / 2, forksLeft - 1, cavernChance / 2, false);
                }

                turn = Clamp(turn + dice.Next(-3, 4), -8, 8);
                yaw += turn;
                pitch = Clamp(pitch + dice.Next(-4, 5), -35, 25);
                if (!leavingEntrance) pitch = pitch * 15 / 16;
                radius = Clamp(radius + dice.Next(-2, 3), 20, 40);

                int horizontal = Sub * Cos(pitch) / 1024;
                x += horizontal * Cos(yaw) / 1024;
                z += horizontal * Sin(yaw) / 1024;
                y += Sub * Sin(pitch) / 1024;
            }
        }

        // A wide, low hollow with a flatter floor, lowered if needed to keep its roof under the ground.
        private void Cavern(ref Dice dice, int x, int y, int z)
        {
            int across = dice.Next(80, 161);
            int up = dice.Next(48, 81);
            int down = dice.Next(24, 41);

            int lowestGround = int.MaxValue;
            for (int corner = 0; corner < 5; corner++)
            {
                int cx = (x + (corner == 1 ? across : corner == 2 ? -across : 0)) >> 4;
                int cz = (z + (corner == 3 ? across : corner == 4 ? -across : 0)) >> 4;
                cx = Clamp(cx, 0, sizeX - 1);
                cz = Clamp(cz, 0, sizeZ - 1);
                lowestGround = Math.Min(lowestGround, landscape.Ground(cx, cz));
            }

            int roof = (lowestGround - CavernRoof) * Sub;
            if (y + up > roof) y = roof - up;
            if (y - down < (LowestRock + 2) * Sub) return;

            caverns.Add(hollows.Count);
            Add(x, y, z, across, up, down);
        }

        // A lava tube: round, flat-floored, following the ground a few blocks down, as long as it
        // stays in basalt.
        private void LavaTube(ref Dice dice, int x, int z)
        {
            int depth = dice.Next(5, 8);
            int yaw = dice.Next(0, 360);
            int turn = 0;
            int radius = dice.Next(30, 41);
            int length = dice.Next(80, 200);
            int px = x * Sub + Sub / 2;
            int pz = z * Sub + Sub / 2;
            int py = (landscape.Ground(x, z) - depth) * Sub + Sub / 2;

            for (int step = 0; step < length; step++)
            {
                int bx = px >> 4;
                int bz = pz >> 4;
                if ((uint)bx >= (uint)sizeX || (uint)bz >= (uint)sizeZ) return;

                Landscape.ColumnGeology column = landscape.Column(bx, bz);
                if (column.Cover != Blocks.Basalt) return;

                int target = (landscape.Ground(bx, bz) - depth) * Sub + Sub / 2;
                py += Clamp(target - py, -Sub / 4, Sub / 4);
                if ((py >> 4) <= column.CoverBottom + 1 || (py >> 4) <= LowestRock + 2) return;

                Add(px, py, pz, radius, radius, radius / 2);

                turn = Clamp(turn + dice.Next(-1, 2), -4, 4);
                yaw += turn;
                radius = Clamp(radius + dice.Next(-1, 2), 28, 42);
                px += Sub * Cos(yaw) / 1024;
                pz += Sub * Sin(yaw) / 1024;
            }
        }

        private void Add(int x, int y, int z, int across, int up, int down)
        {
            hollows.Add(new Hollow { X = x, Y = y, Z = z, Across = across, Up = up, Down = down });
        }

        private void IndexHollows()
        {
            int chunksZ = (sizeZ + Chunk.Size - 1) / Chunk.Size;
            var lists = new List<int>[chunksX * chunksZ];
            for (int index = 0; index < hollows.Count; index++)
            {
                Hollow hollow = hollows[index];
                int minX = Math.Max(0, (hollow.X - hollow.Across) >> 8);
                int maxX = Math.Min(chunksX - 1, (hollow.X + hollow.Across) >> 8);
                int minZ = Math.Max(0, (hollow.Z - hollow.Across) >> 8);
                int maxZ = Math.Min(chunksZ - 1, (hollow.Z + hollow.Across) >> 8);
                for (int cz = minZ; cz <= maxZ; cz++)
                {
                    for (int cx = minX; cx <= maxX; cx++)
                    {
                        int column = cx + chunksX * cz;
                        if (lists[column] == null) lists[column] = new List<int>();
                        lists[column].Add(index);
                    }
                }
            }

            byColumn = new int[lists.Length][];
            for (int i = 0; i < lists.Length; i++)
            {
                byColumn[i] = lists[i] == null ? Array.Empty<int>() : lists[i].ToArray();
            }
        }

        // Some caverns hold a lake: water up to a level a block or two above their floor, in every
        // hollow block joined to it below that level. The joined blocks are found in a box around
        // the cavern; a lake that reaches the box's sides, the open air or too many blocks is given up.
        private void FillLakes(uint seed)
        {
            for (int number = 0; number < caverns.Count; number++)
            {
                Hollow cavern = hollows[caverns[number]];
                var dice = new Dice(Hash(number, 0, 0, seed ^ LakeSeedMask));
                int centreX = cavern.X >> 4;
                int centreZ = cavern.Z >> 4;
                ushort rock = landscape.Column(centreX, centreZ).Cover;
                int chance = rock == Blocks.Limestone ? KarstLakeChance : rock == Blocks.Basalt ? 0 : OtherLakeChance;
                if (dice.Next(0, 1024) >= chance) continue;

                int floor = (cavern.Y - cavern.Down) >> 4;
                int level = floor + dice.Next(1, 3);
                List<long> lake = FindLake(centreX, centreZ, floor, level);
                if (lake == null) continue;

                foreach (long block in lake)
                {
                    lakeBlocks.Add(block);
                }

                LakeCount++;
            }
        }

        private List<long> FindLake(int centreX, int centreZ, int floor, int level)
        {
            int startY = -1;
            for (int y = Math.Max(floor, LowestRock + 1); y <= level; y++)
            {
                if (IsHollow(centreX, y, centreZ))
                {
                    startY = y;
                    break;
                }
            }

            if (startY < 0) return null;

            // The hollow blocks of the box, from the bottom of the world up to the level.
            int minX = centreX - LakeReach;
            int minZ = centreZ - LakeReach;
            int width = 2 * LakeReach + 1;
            int height = level + 1;
            var hollow = new bool[width * width * height];
            MarkHollows(hollow, minX, minZ, width, level);

            var lake = new List<long>();
            var seen = new bool[hollow.Length];
            var queue = new Queue<int>();
            int start = (centreX - minX) + width * ((centreZ - minZ) + width * startY);
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int cell = queue.Dequeue();
                int x = cell % width;
                int z = cell / width % width;
                int y = cell / (width * width);
                int worldX = minX + x;
                int worldZ = minZ + z;

                // Open air, or a lake that runs out of the box: not a lake that rock can hold.
                if (y > landscape.Ground(worldX, worldZ)) return null;
                if (x == 0 || z == 0 || x == width - 1 || z == width - 1) return null;

                lake.Add(Pack(worldX, y, worldZ));
                if (lake.Count > MaxLakeBlocks) return null;

                TryVisit(cell + 1);
                TryVisit(cell - 1);
                TryVisit(cell + width);
                TryVisit(cell - width);
                if (y > 0) TryVisit(cell - width * width);
                if (y < level) TryVisit(cell + width * width);
            }

            return lake;

            void TryVisit(int next)
            {
                if (seen[next] || !hollow[next]) return;
                seen[next] = true;
                queue.Enqueue(next);
            }
        }

        // Marks the hollow blocks of a box (width × width columns from minX, minZ; heights up to
        // top), going through the hollows that reach it once each.
        private void MarkHollows(bool[] box, int minX, int minZ, int width, int top)
        {
            stamp++;
            if (marks == null || marks.Length < hollows.Count) marks = new int[hollows.Count];

            int chunksZ = (sizeZ + Chunk.Size - 1) / Chunk.Size;
            int firstX = Math.Max(0, minX >> Chunk.SizeBits);
            int lastX = Math.Min(chunksX - 1, (minX + width - 1) >> Chunk.SizeBits);
            int firstZ = Math.Max(0, minZ >> Chunk.SizeBits);
            int lastZ = Math.Min(chunksZ - 1, (minZ + width - 1) >> Chunk.SizeBits);
            for (int cz = firstZ; cz <= lastZ; cz++)
            {
                for (int cx = firstX; cx <= lastX; cx++)
                {
                    foreach (int index in byColumn[cx + chunksX * cz])
                    {
                        if (marks[index] == stamp) continue;

                        marks[index] = stamp;
                        Hollow hollow = hollows[index];
                        int fromX = Math.Max(minX, (hollow.X - hollow.Across) >> 4);
                        int toX = Math.Min(minX + width - 1, (hollow.X + hollow.Across) >> 4);
                        int fromZ = Math.Max(minZ, (hollow.Z - hollow.Across) >> 4);
                        int toZ = Math.Min(minZ + width - 1, (hollow.Z + hollow.Across) >> 4);
                        int fromY = Math.Max(LowestRock + 1, (hollow.Y - hollow.Down) >> 4);
                        int toY = Math.Min(top, (hollow.Y + hollow.Up) >> 4);
                        for (int y = fromY; y <= toY; y++)
                        {
                            for (int z = fromZ; z <= toZ; z++)
                            {
                                for (int x = fromX; x <= toX; x++)
                                {
                                    if (!MayCarve(x, y, z) || !Inside(hollow, x * Sub + Sub / 2, y * Sub + Sub / 2, z * Sub + Sub / 2)) continue;

                                    box[(x - minX) + width * ((z - minZ) + width * y)] = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        private long Pack(int x, int y, int z) => x + (long)sizeX * (z + (long)sizeZ * y);

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        // Sine of an angle in whole degrees, in 1/1024, from Bhaskara's integer approximation
        // (within 0.002): no floating point, so every machine gets the same value.
        private static int Sin(int degrees)
        {
            int angle = ((degrees % 360) + 360) % 360;
            int sign = 1;
            if (angle >= 180)
            {
                angle -= 180;
                sign = -1;
            }

            int product = angle * (180 - angle);
            return sign * 4 * product * 1024 / (40500 - product);
        }

        private static int Cos(int degrees) => Sin(degrees + 90);

        private static uint Hash(int x, int z, int w, uint seed)
        {
            unchecked
            {
                uint hash = seed ^ 0x9E3779B9u;
                hash = (hash ^ (uint)x) * 0x85EBCA6Bu;
                hash = (hash ^ (hash >> 13) ^ (uint)z) * 0xC2B2AE35u;
                hash = (hash ^ (hash >> 16) ^ (uint)w) * 0x27D4EB2Fu;
                hash ^= hash >> 15;
                return hash;
            }
        }
    }
}
