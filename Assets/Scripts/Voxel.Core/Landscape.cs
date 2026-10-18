using System;

namespace Voxel.Core
{
    // The world's ground and water as generation lays them out, worked out once for the whole
    // world from its seed and size: the relief (TerrainGenerator.ReliefHeight) lowered into lake
    // basins and, along the west edge, into a sea; lakes filled up to where they would spill;
    // rivers that gather the rain of the land and run down through lakes to the sea or the
    // world's edge; the climate (Geography) over all of it; marshes, with their shallow pools, on
    // low land where it is very wet; and ice where the water is cold.
    //
    // Like the terrain, everything is integer and done in a fixed order, so the server and every
    // client get exactly the same landscape without anything being sent.
    //
    // Water finds its way with a priority flood: from the world's edges, held at sea level, the
    // land is visited from the lowest point inwards; a depression is filled to the height of its
    // rim (a lake), and each column remembers the neighbour it was reached from, which is where its
    // water flows. Adding up, against that order, the columns draining through each column gives
    // its catchment; where it is large, a river is carved. Heights are compared in 1/256 block
    // with a little smooth noise added, so that water on flat land wanders instead of running in
    // straight lines.
    public sealed class Landscape
    {
        // Height of the surface of the sea and of lowland lakes: that of the plains' ground, so
        // shores are flush with the water.
        public const int SeaLevel = 20;

        private const int FixedShift = 8;

        // Lake basins: wide hollows in about one tenth of the land, at most BasinDepth deep.
        private const int BasinCellShift = 7;
        private const int BasinFloor = IntNoise.One * 60 / 100;
        private const int BasinGainTenths = 25;
        private const int BasinDepth = 9;
        private const uint BasinSeedMask = 0x5BD1E995u;

        // The sea along the west edge, which the wind comes from: its width follows the world's,
        // its shore wanders, and the sea floor goes down to CoastDepth blocks at the edge.
        private const int MinCoastWidth = 24;
        private const int MaxCoastWidth = 96;
        private const int CoastDepth = 10;
        private const int CoastCellShift = 6;
        private const uint CoastSeedMask = 0x1B873593u;

        // Noise added to heights for the water's way across flat land: wanderings of about 32 blocks.
        private const int MeanderCellShift = 5;
        private const int MeanderOctaves = 2;
        private const uint MeanderSeedMask = 0xCC9E2D51u;

        // For rivers only, the same noise stretched to this many blocks (in tenths).
        private const int RiverMeanderBlocksTenths = 25;

        // Smaller bodies of standing water are filled up instead of kept as lakes.
        private const int MinLakeColumns = 150;

        // Catchment, in columns, from which a river flows; a wider one; a deeper one.
        private const int RiverColumns = 3000;
        private const int WideRiverColumns = 15000;
        private const int DeepRiverColumns = 40000;

        // Rock is bare at altitude and on steep slopes.
        private const int RockAltitude = 46;
        private const int RockSlope = 3;

        // Water colder than this freezes at its surface.
        private const int IceTemperature = IntNoise.One * 20 / 100;

        // Geology. Provinces of about 256 blocks; the volcanic ones are about one in ten.
        private const int ProvinceCellShift = 8;
        private const int VolcanicFloor = IntNoise.One * 72 / 100;
        private const uint VolcanicSeedMask = 0x68E31DA4u;

        // Regions: dry and warm (sandstone, sand), cold (slate); snow where it is colder still, or high and cool.
        private const int DryHumidity = IntNoise.One * 28 / 100;
        private const int DryTemperature = IntNoise.One * 50 / 100;
        private const int ColdTemperature = IntNoise.One * 30 / 100;
        private const int SnowTemperature = IntNoise.One * 8 / 100;
        private const int HighSnowTemperature = IntNoise.One * 45 / 100;
        private const int SnowLine = 50;

        // Thickness of the cover rock, varying over about 64 blocks; above MassifCore it thins by
        // CoverThinning blocks per block of height.
        private const int MinCover = 8;
        private const int MaxCover = 24;
        private const int CoverCellShift = 6;
        private const uint CoverSeedMask = 0xB5297A4Du;
        private const int MassifCore = 40;
        private const int CoverThinning = 3;

        // Marble: at the base of thin limestone (at most MarbleCover thick) on ground this high, two blocks thick.
        private const int MarbleHeight = 30;
        private const int MarbleCover = 12;

        // Dirt and sand under the top block, as TerrainGenerator lays them.
        private const int TerrainDirtDepth = 3;

        private const int BeachWidth = 2;

        // Marshes: low, gentle and very wet land that is not cold, its edges ragged by noise of about
        // 32 blocks (which moves the humidity threshold by up to a tenth either way); only stretches
        // of at least MinMarshColumns. Within them, pools one block deep where noise of about 4
        // blocks is high and the ground is flat all around.
        private const int MarshHumidity = IntNoise.One * 66 / 100;
        private const int MarshTemperature = IntNoise.One * 25 / 100;
        private const int MarshHighest = SeaLevel + 4;
        private const int MarshCellShift = 5;
        private const int MarshEdgeTenths = 2;
        private const uint MarshSeedMask = 0x3C6EF372u;
        private const int MinMarshColumns = 300;
        private const int PoolCellShift = 2;
        private const int PoolFloor = IntNoise.One * 50 / 100;
        private const uint PoolSeedMask = 0x9E3779B9u;

        private static Landscape last;

        private readonly int seed;
        private readonly int sizeX;
        private readonly int sizeZ;

        // Per block column: the topmost solid block, the topmost water block (or -1), bare rock, ice on top.
        private readonly short[] ground;
        private readonly short[] water;
        private readonly bool[] rock;
        private readonly bool[] ice;

        // Per block column: what holds its water (WaterKind), and its geology (see Column).
        private readonly byte[] kind;
        private readonly ushort[] top;
        private readonly ushort[] under;
        private readonly ushort[] cover;
        private readonly short[] coverBottom;
        private readonly bool[] marble;

        // Per block column: part of a marsh, its pools or the land between them.
        private readonly bool[] marsh;

        private Landscape(int seed, int sizeX, int sizeZ)
        {
            this.seed = seed;
            this.sizeX = sizeX;
            this.sizeZ = sizeZ;
            int count = sizeX * sizeZ;
            ground = new short[count];
            water = new short[count];
            rock = new bool[count];
            ice = new bool[count];
            kind = new byte[count];
            top = new ushort[count];
            under = new ushort[count];
            cover = new ushort[count];
            coverBottom = new short[count];
            marble = new bool[count];
            marsh = new bool[count];
        }

        // What a column's water is part of.
        private const byte Dry = 0;
        private const byte SeaWater = 1;
        private const byte LakeWater = 2;
        private const byte RiverWater = 3;
        private const byte MarshWater = 4;

        // A column's geology, as TerrainGenerator lays it out from the bottom up: bedrock, granite,
        // then from CoverBottom the cover rock (with a band of marble at its base where Marble is
        // set) up to three blocks under the ground, then Under, then Top at Ground. Bare rock
        // columns have rock up to their top (or snow on it when Top is snow). Under water, Top and
        // Under are the bed. Water and ice above as WaterTop and IsIce say.
        public struct ColumnGeology
        {
            public ushort Top;
            public ushort Under;
            public ushort Cover;
            public int CoverBottom;
            public bool Marble;

            // The region is cold: tin is found in its granite.
            public bool Cold;
        }

        public ColumnGeology Column(int x, int z)
        {
            int index = x + sizeX * z;
            return new ColumnGeology
            {
                Top = top[index],
                Under = under[index],
                Cover = cover[index],
                CoverBottom = coverBottom[index],
                Marble = marble[index],
                Cold = cover[index] == Blocks.Slate
            };
        }

        public Geography Geography { get; private set; }

        // The caves under this landscape, which TerrainGenerator carves into each chunk.
        public Caves Caves { get; private set; }

        // The landscape of this seed and size. The last one worked out is kept: the generator and
        // the world's climate ask for it in turn.
        public static Landscape For(int seed, int sizeX, int sizeZ)
        {
            Landscape cached = last;
            if (cached != null && cached.seed == seed && cached.sizeX == sizeX && cached.sizeZ == sizeZ) return cached;

            var landscape = new Landscape(seed, sizeX, sizeZ);
            landscape.Build();
            last = landscape;
            return landscape;
        }

        // Height of the topmost solid block of a column inside the world.
        public int Ground(int x, int z) => ground[x + sizeX * z];

        // Height of the topmost water block of the column, or -1 if it is dry.
        public int WaterTop(int x, int z) => water[x + sizeX * z];

        public bool IsRock(int x, int z) => rock[x + sizeX * z];

        // Whether the topmost water block is ice.
        public bool IsIce(int x, int z) => ice[x + sizeX * z];

        // Whether the column is part of a marsh: one of its pools or the land between them.
        public bool IsMarsh(int x, int z) => marsh[x + sizeX * z];

        // Whether the column's water is a marsh pool (one block deep).
        public bool IsMarshPool(int x, int z) => kind[x + sizeX * z] == MarshWater;

        private void Build()
        {
            int count = sizeX * sizeZ;
            var relief = new int[count];
            var elevation = new int[count];
            int coastWidth = Math.Min(MaxCoastWidth, Math.Max(MinCoastWidth, sizeX / 8));
            uint noiseSeed = unchecked((uint)seed);

            for (int z = 0; z < sizeZ; z++)
            {
                int shore = coastWidth + (int)(((long)IntNoise.Sample(z, 0, CoastCellShift, noiseSeed ^ CoastSeedMask) - IntNoise.One / 2) * coastWidth / IntNoise.One);
                for (int x = 0; x < sizeX; x++)
                {
                    int height = TerrainGenerator.ReliefHeight(x, z, seed) - BasinDepthAt(x, z, noiseSeed);

                    // Towards the west edge the land sinks under the sea, whatever its relief.
                    if (x < shore) height -= (height - (SeaLevel - CoastDepth)) * (shore - x) / Math.Max(1, shore);

                    int index = x + sizeX * z;
                    relief[index] = Math.Max(2, height);
                    elevation[index] = (relief[index] << FixedShift) + MeanderAt(x, z, noiseSeed);
                }
            }

            Flood(elevation, out int[] filled, out int[] downstream, out int[] order);

            var lake = new bool[count];
            for (int index = 0; index < count; index++)
            {
                int level = filled[index] >> FixedShift;
                lake[index] = level > relief[index];
            }

            // Small hollows do not hold a lake: they are filled up, as earth washed in would.
            FillSmallLakes(relief, filled, lake);
            for (int index = 0; index < count; index++)
            {
                ground[index] = (short)relief[index];
                water[index] = (short)(lake[index] ? filled[index] >> FixedShift : -1);
            }

            // The filled hollows are flat, which the first flood saw as still water: the way water
            // runs is worked out again over the final relief, so that rivers wander across them too.
            // This time the noise reaches a few blocks, so that rivers also cut through low steps
            // instead of following narrow strips of low ground in straight lines; it only decides
            // where rivers run, never how high water stands.
            for (int index = 0; index < count; index++)
            {
                int meander = elevation[index] & ((1 << FixedShift) - 1);
                elevation[index] = (relief[index] << FixedShift) + meander * RiverMeanderBlocksTenths / 10;
            }

            Flood(elevation, out filled, out downstream, out order);
            CarveRivers(relief, lake, downstream, order);
            MarkRock();

            Geography = Geography.Create(seed, sizeX, sizeZ,
                (x, z) => Math.Max(Ground(x, z), WaterTop(x, z)),
                (x, z) => WaterTop(x, z) >= 0);

            // Marshes come from the climate, so the climate is that of the land before them.
            LayMarshes(noiseSeed);
            MarkIce();
            LayGeology(noiseSeed);
            Caves = new Caves(seed, this, sizeX, sizeZ);
        }

        // The rocks under each column and what covers its ground. The cover rock is that of the
        // region: limestone where it is mild, sandstone where it is dry and warm, slate where it is
        // cold, basalt in the rare volcanic provinces. It is 8 to 24 blocks thick and thins out on
        // high ground, so the massifs' granite core shows on their heights; where limestone lies
        // thin on granite near the massifs, its base has turned to marble.
        private void LayGeology(uint noiseSeed)
        {
            bool[] nearOpenWater = NearOpenWater();
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int index = x + sizeX * z;
                    int height = ground[index];
                    long warmth = Geography.TemperatureAt(x, z);
                    long wetness = Geography.HumidityAt(x, z);
                    bool volcanic = IntNoise.TurnedFractal(x, z, ProvinceCellShift, 1, noiseSeed ^ VolcanicSeedMask) >= VolcanicFloor;
                    bool dry = wetness < DryHumidity && warmth > DryTemperature;
                    bool cold = warmth < ColdTemperature;

                    ushort coverRock = volcanic ? Blocks.Basalt : dry ? Blocks.Sandstone : cold ? Blocks.Slate : Blocks.Limestone;
                    int thickness = MinCover + (int)((long)IntNoise.TurnedFractal(x, z, CoverCellShift, 2, noiseSeed ^ CoverSeedMask) * (MaxCover - MinCover) >> 16);
                    if (height > MassifCore) thickness = Math.Max(0, thickness - (height - MassifCore) * CoverThinning);

                    cover[index] = coverRock;
                    coverBottom[index] = (short)(height - TerrainDirtDepth - thickness);
                    marble[index] = coverRock == Blocks.Limestone && height >= MarbleHeight && thickness > 0 && thickness <= MarbleCover;

                    bool snowy = warmth < SnowTemperature || (height >= SnowLine && warmth < HighSnowTemperature);
                    if (water[index] > height)
                    {
                        ushort bed = kind[index] == RiverWater ? Blocks.Gravel
                            : kind[index] == SeaWater ? Blocks.Sand
                            : kind[index] == MarshWater ? Blocks.Mud
                            : Blocks.Clay;
                        top[index] = bed;
                        under[index] = bed;
                    }
                    else if (marsh[index])
                    {
                        top[index] = Blocks.Grass;
                        under[index] = Blocks.Mud;
                    }
                    else if (snowy)
                    {
                        top[index] = Blocks.Snow;
                        under[index] = Blocks.Dirt;
                    }
                    else if (dry || (nearOpenWater[index] && height <= SeaLevel + 1))
                    {
                        top[index] = Blocks.Sand;
                        under[index] = Blocks.Sand;
                    }
                    else
                    {
                        top[index] = Blocks.Grass;
                        under[index] = Blocks.Dirt;
                    }
                }
            }
        }

        // Columns within two of the water of a lake or the sea (not a river): where beaches are.
        private bool[] NearOpenWater()
        {
            var near = new bool[sizeX * sizeZ];
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    byte body = kind[x + sizeX * z];
                    if (body != SeaWater && body != LakeWater) continue;

                    for (int dz = -BeachWidth; dz <= BeachWidth; dz++)
                    {
                        for (int dx = -BeachWidth; dx <= BeachWidth; dx++)
                        {
                            int ox = x + dx, oz = z + dz;
                            if ((uint)ox < (uint)sizeX && (uint)oz < (uint)sizeZ) near[ox + sizeX * oz] = true;
                        }
                    }
                }
            }

            return near;
        }

        // Marshes spread over low, gentle, dry land where it is very wet and not cold; small stretches
        // are left out. In them, a pool one block deep is dug where no ground around is lower:
        // its water stands where its ground was, no higher than the ground beside it, so it is always
        // held, and pools never touch other water, whose surface may be lower.
        private void LayMarshes(uint noiseSeed)
        {
            int count = sizeX * sizeZ;
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int index = x + sizeX * z;
                    int height = ground[index];
                    if (water[index] >= 0 || rock[index] || height > MarshHighest || !IsGentle(x, z, height)) continue;
                    if (Geography.TemperatureAt(x, z) < MarshTemperature) continue;

                    int edge = (IntNoise.TurnedFractal(x, z, MarshCellShift, 2, noiseSeed ^ MarshSeedMask) - IntNoise.One / 2) * MarshEdgeTenths / 10;
                    marsh[index] = Geography.HumidityAt(x, z) + edge >= MarshHumidity;
                }
            }

            DropSmallAreas(marsh, MinMarshColumns);

            var pool = new bool[count];
            for (int z = 1; z < sizeZ - 1; z++)
            {
                for (int x = 1; x < sizeX - 1; x++)
                {
                    int index = x + sizeX * z;
                    if (!marsh[index] || IntNoise.TurnedFractal(x, z, PoolCellShift, 2, noiseSeed ^ PoolSeedMask) < PoolFloor) continue;

                    // Two pools side by side are thus at the same height: each needs the other to be no lower.
                    bool held = true;
                    for (int dz = -1; dz <= 1 && held; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int around = index + dx + sizeX * dz;
                            if (ground[around] < ground[index] || water[around] >= 0)
                            {
                                held = false;
                                break;
                            }
                        }
                    }

                    pool[index] = held;
                }
            }

            for (int index = 0; index < count; index++)
            {
                if (!pool[index]) continue;

                water[index] = ground[index];
                ground[index]--;
                kind[index] = MarshWater;
            }
        }

        // Neighbours at most one block higher or lower.
        private bool IsGentle(int x, int z, int height)
        {
            return Math.Abs(GroundOrSame(x - 1, z, height) - height) <= 1 && Math.Abs(GroundOrSame(x + 1, z, height) - height) <= 1
                   && Math.Abs(GroundOrSame(x, z - 1, height) - height) <= 1 && Math.Abs(GroundOrSame(x, z + 1, height) - height) <= 1;
        }

        // Clears the stretches of the mask (columns joined side by side) smaller than minColumns.
        private void DropSmallAreas(bool[] mask, int minColumns)
        {
            int count = mask.Length;
            var seen = new bool[count];
            var area = new System.Collections.Generic.List<int>();
            var stack = new System.Collections.Generic.Stack<int>();

            for (int start = 0; start < count; start++)
            {
                if (!mask[start] || seen[start]) continue;

                area.Clear();
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    area.Add(index);
                    int x = index % sizeX;
                    int z = index / sizeX;
                    Join(x + 1, z, mask, seen, stack);
                    Join(x - 1, z, mask, seen, stack);
                    Join(x, z + 1, mask, seen, stack);
                    Join(x, z - 1, mask, seen, stack);
                }

                if (area.Count >= minColumns) continue;

                foreach (int index in area)
                {
                    mask[index] = false;
                }
            }
        }

        // Less than a block of smooth noise, without straight lines that water would follow.
        private static int MeanderAt(int x, int z, uint noiseSeed)
        {
            return IntNoise.TurnedFractal(x, z, MeanderCellShift, MeanderOctaves, noiseSeed ^ MeanderSeedMask) >> (16 - FixedShift);
        }

        private static int BasinDepthAt(int x, int z, uint noiseSeed)
        {
            long value = IntNoise.TurnedFractal(x, z, BasinCellShift, 1, noiseSeed ^ BasinSeedMask);
            long stretched = Math.Min(Math.Max((value - BasinFloor) * BasinGainTenths / 10, 0), IntNoise.One - 1);
            return (int)(stretched * BasinDepth >> 16);
        }

        // Priority flood from the world's edges, held at sea level. filled: the height water rises to
        // in each column (its own height where it is dry); downstream: the neighbour it drains to (-1
        // on the edges); order: the columns from the first visited (lowest) to the last.
        private void Flood(int[] elevation, out int[] filled, out int[] downstream, out int[] order)
        {
            int count = elevation.Length;
            filled = new int[count];
            downstream = new int[count];
            order = new int[count];
            var visited = new bool[count];
            var queue = new LowestFirst(count);
            int sea = SeaLevel << FixedShift;

            // Water leaves the world by any edge, as if the sea surrounded it; along the west edge
            // the land is low enough for that sea to show. Holding water in at the other edges would
            // fill every hollow touching them into an inland sea.
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    if (x != 0 && z != 0 && x != sizeX - 1 && z != sizeZ - 1) continue;

                    int index = x + sizeX * z;
                    visited[index] = true;
                    filled[index] = Math.Max(elevation[index], sea);
                    downstream[index] = -1;
                    queue.Push(filled[index], index);
                }
            }

            int visitedCount = 0;
            while (queue.Count > 0)
            {
                int index = queue.Pop();
                order[visitedCount++] = index;
                int x = index % sizeX;
                int z = index / sizeX;
                Visit(index, x + 1, z, elevation, filled, downstream, visited, queue);
                Visit(index, x - 1, z, elevation, filled, downstream, visited, queue);
                Visit(index, x, z + 1, elevation, filled, downstream, visited, queue);
                Visit(index, x, z - 1, elevation, filled, downstream, visited, queue);
            }
        }

        private void Visit(int from, int x, int z, int[] elevation, int[] filled, int[] downstream, bool[] visited, LowestFirst queue)
        {
            if ((uint)x >= (uint)sizeX || (uint)z >= (uint)sizeZ) return;

            int index = x + sizeX * z;
            if (visited[index]) return;

            visited[index] = true;
            filled[index] = Math.Max(elevation[index], filled[from]);
            downstream[index] = from;
            queue.Push(filled[index], index);
        }

        // Finds each body of standing water (columns joined side by side) in a fixed order; those
        // smaller than MinLakeColumns become dry land at their water's level.
        private void FillSmallLakes(int[] relief, int[] filled, bool[] lake)
        {
            int count = relief.Length;
            var seen = new bool[count];
            var body = new System.Collections.Generic.List<int>();
            var stack = new System.Collections.Generic.Stack<int>();

            for (int start = 0; start < count; start++)
            {
                if (!lake[start] || seen[start]) continue;

                body.Clear();
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    body.Add(index);
                    int x = index % sizeX;
                    int z = index / sizeX;
                    Join(x + 1, z, lake, seen, stack);
                    Join(x - 1, z, lake, seen, stack);
                    Join(x, z + 1, lake, seen, stack);
                    Join(x, z - 1, lake, seen, stack);
                }

                if (body.Count >= MinLakeColumns)
                {
                    // Water reaching the west edge is the sea.
                    bool sea = false;
                    foreach (int index in body)
                    {
                        if (index % sizeX == 0) sea = true;
                    }

                    foreach (int index in body)
                    {
                        kind[index] = sea ? SeaWater : LakeWater;
                    }

                    continue;
                }

                foreach (int index in body)
                {
                    relief[index] = filled[index] >> FixedShift;
                    lake[index] = false;
                }
            }
        }

        private void Join(int x, int z, bool[] lake, bool[] seen, System.Collections.Generic.Stack<int> stack)
        {
            if ((uint)x >= (uint)sizeX || (uint)z >= (uint)sizeZ) return;

            int index = x + sizeX * z;
            if (!lake[index] || seen[index]) return;

            seen[index] = true;
            stack.Push(index);
        }

        // Rivers run where the catchment is large, flush with the lowest ground around them, one
        // or two blocks deep, three columns wide when large. Lakes and the sea already hold water.
        private void CarveRivers(int[] relief, bool[] lake, int[] downstream, int[] order)
        {
            int count = relief.Length;
            var catchment = new int[count];
            for (int i = 0; i < count; i++)
            {
                catchment[i] = 1;
            }

            // From the last visited (highest upstream) to the first: each column passes on what drains through it.
            for (int k = count - 1; k >= 0; k--)
            {
                int index = order[k];
                if (downstream[index] >= 0) catchment[downstream[index]] += catchment[index];
            }

            for (int k = 0; k < count; k++)
            {
                int index = order[k];
                if (catchment[index] < RiverColumns || lake[index]) continue;

                int x = index % sizeX;
                int z = index / sizeX;
                int radius = catchment[index] >= WideRiverColumns ? 1 : 0;
                int depth = catchment[index] >= DeepRiverColumns ? 2 : 1;

                // Flush with the lowest ground around, so that no water stands above its banks.
                int surface = int.MaxValue;
                for (int dz = -radius - 1; dz <= radius + 1; dz++)
                {
                    for (int dx = -radius - 1; dx <= radius + 1; dx++)
                    {
                        int ox = x + dx, oz = z + dz;
                        if ((uint)ox < (uint)sizeX && (uint)oz < (uint)sizeZ) surface = Math.Min(surface, relief[ox + sizeX * oz]);
                    }
                }

                for (int dz = -radius; dz <= radius; dz++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int ox = x + dx, oz = z + dz;
                        if ((uint)ox >= (uint)sizeX || (uint)oz >= (uint)sizeZ) continue;

                        int column = ox + sizeX * oz;
                        if (lake[column]) continue;

                        ground[column] = (short)Math.Min(ground[column], surface - depth);
                        water[column] = (short)Math.Max(water[column], surface);
                        kind[column] = RiverWater;
                    }
                }
            }
        }

        private void MarkRock()
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int index = x + sizeX * z;
                    int height = ground[index];
                    int slope = Math.Max(
                        Math.Max(Math.Abs(GroundOrSame(x - 1, z, height) - height), Math.Abs(GroundOrSame(x + 1, z, height) - height)),
                        Math.Max(Math.Abs(GroundOrSame(x, z - 1, height) - height), Math.Abs(GroundOrSame(x, z + 1, height) - height)));
                    rock[index] = water[index] < 0 && (height >= RockAltitude || slope >= RockSlope);
                }
            }
        }

        private int GroundOrSame(int x, int z, int fallback)
        {
            return (uint)x < (uint)sizeX && (uint)z < (uint)sizeZ ? ground[x + sizeX * z] : fallback;
        }

        private void MarkIce()
        {
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    int index = x + sizeX * z;
                    ice[index] = water[index] >= 0 && Geography.TemperatureAt(x, z) < IceTemperature;
                }
            }
        }

        // Columns waiting to be visited, lowest first; ties go to the lower index, so the order
        // never depends on the machine.
        private sealed class LowestFirst
        {
            private readonly long[] heap;

            public LowestFirst(int capacity)
            {
                heap = new long[capacity];
            }

            public int Count { get; private set; }

            public void Push(int height, int index)
            {
                long key = ((long)height << 32) | (uint)index;
                int child = Count++;
                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (heap[parent] <= key) break;

                    heap[child] = heap[parent];
                    child = parent;
                }

                heap[child] = key;
            }

            public int Pop()
            {
                long top = heap[0];
                long last = heap[--Count];
                int parent = 0;
                while (true)
                {
                    int child = 2 * parent + 1;
                    if (child >= Count) break;
                    if (child + 1 < Count && heap[child + 1] < heap[child]) child++;
                    if (heap[child] >= last) break;

                    heap[parent] = heap[child];
                    parent = child;
                }

                if (Count > 0) heap[parent] = last;
                return (int)(top & 0xFFFFFFFF);
            }
        }
    }
}
