using System;

namespace Voxel.Core
{
    // The world's climate as generation lays it out: temperature and humidity on a coarse grid of
    // cells 8 × 8 block columns wide, worked out once for the whole world from its seed and size.
    // Like the terrain, it only uses integers (IntNoise, fixed point with IntNoise.One standing for
    // 1) and a fixed order of computation, so the server and every client get exactly the same
    // climate without anything being sent.
    //
    // Temperature falls from south (z = 0, hot) to north (cold), varies over zones of about 256
    // blocks, and drops with altitude. Humidity has its own zones, and the wind blows from the
    // west: air loses its moisture as rain on slopes that rise in its way and gets it back slowly
    // over flat land, so the lee side of a massif is dry (rain shadow). Land near lakes, rivers and
    // the sea is wetter.
    public sealed class Geography
    {
        public const int CellShift = 3;
        public const int CellSize = 1 << CellShift;

        // Zones of about 256 blocks.
        private const int ZoneCellShift = 8;
        private const int ZoneOctaves = 3;
        private const uint TemperatureSeedMask = 0x3C6EF372u;
        private const uint HumiditySeedMask = 0xA54FF53Au;

        // Share of the north-south gradient in the temperature, in percent; the rest is the zones' noise.
        private const int GradientPercent = 60;

        // Above this height the air cools, by IntNoise.One per CoolingBlocks blocks.
        private const int CoolingStart = 28;
        private const int CoolingBlocks = 64;

        // Rain shadow: share of the air's moisture lost per block of rise (in 1/RainDivisor, at most
        // MaxRise blocks counted per cell), and the share of what is missing it gets back per cell.
        private const int RainDivisor = 12;
        private const int MaxRise = 8;
        private const int RechargeCells = 24;

        // Blur applied to the air's moisture and the rain, in cells on each side: more across the
        // wind (between rows) than along it.
        private const int AirBlurCellsX = 1;
        private const int AirBlurCellsZ = 3;

        // Land near water is wetter: the share of water within WaterBlurCells cells around, times
        // this much humidity at most.
        private const int WaterBlurCells = 3;
        private const int WaterHumidity = IntNoise.One * 45 / 100;

        // Humidity left to land under completely dry air, and how far the zones' noise is stretched, in percent.
        private const int DryAirShare = IntNoise.One * 35 / 100;
        private const int ContrastPercent = 220;

        private readonly int[] heights;
        private readonly int[] temperatures;
        private readonly int[] humidities;

        private Geography(int cellsX, int cellsZ)
        {
            CellsX = cellsX;
            CellsZ = cellsZ;
            heights = new int[cellsX * cellsZ];
            temperatures = new int[cellsX * cellsZ];
            humidities = new int[cellsX * cellsZ];
        }

        public int CellsX { get; }
        public int CellsZ { get; }

        // The climate of the world TerrainGenerator makes from this seed and size (in blocks).
        public static Geography Create(int seed, int sizeX, int sizeZ)
        {
            return Landscape.For(seed, sizeX, sizeZ).Geography;
        }

        // The climate over any relief: surfaceHeight gives the height of a block column, isWater
        // whether it is under water (water makes the air around it wetter), or null for dry land.
        public static Geography Create(int seed, int sizeX, int sizeZ, Func<int, int, int> surfaceHeight, Func<int, int, bool> isWater = null)
        {
            if (sizeX < 2 * CellSize) throw new ArgumentOutOfRangeException(nameof(sizeX));
            if (sizeZ < 2 * CellSize) throw new ArgumentOutOfRangeException(nameof(sizeZ));

            var geography = new Geography(sizeX >> CellShift, sizeZ >> CellShift);
            geography.Fill(unchecked((uint)seed), surfaceHeight, isWater);
            return geography;
        }

        // Mean height of the ground in the cell, in blocks, as generated (edits do not change it).
        public int Height(int cellX, int cellZ) => heights[CellIndex(cellX, cellZ)];

        // In [0, IntNoise.One): 0 is the coldest, IntNoise.One the hottest.
        public int Temperature(int cellX, int cellZ) => temperatures[CellIndex(cellX, cellZ)];

        // In [0, IntNoise.One): 0 is the driest, IntNoise.One the wettest.
        public int Humidity(int cellX, int cellZ) => humidities[CellIndex(cellX, cellZ)];

        // At a block column, blended between the centres of the nearest cells, so that the climate
        // changes smoothly from one column to the next.
        public int TemperatureAt(int x, int z) => Blend(temperatures, x, z);

        public int HumidityAt(int x, int z) => Blend(humidities, x, z);

        private void Fill(uint seed, Func<int, int, int> surfaceHeight, Func<int, int, bool> isWater)
        {
            FillHeights(surfaceHeight);
            int[] waterNearby = Blur(WaterShares(isWater), WaterBlurCells, WaterBlurCells);

            var airMoisture = new int[heights.Length];
            var rainfall = new int[heights.Length];
            for (int cellZ = 0; cellZ < CellsZ; cellZ++)
            {
                // From south (hot) to north (cold).
                long gradient = IntNoise.One - 1 - (long)cellZ * IntNoise.One / CellsZ;
                long moisture = IntNoise.One;
                int previousHeight = heights[CellIndex(0, cellZ)];

                // West to east, the way the wind blows: the order matters and never changes.
                for (int cellX = 0; cellX < CellsX; cellX++)
                {
                    int index = CellIndex(cellX, cellZ);
                    int height = heights[index];

                    long zones = Contrast(IntNoise.Fractal(CentreOf(cellX), CentreOf(cellZ), ZoneCellShift, ZoneOctaves, seed ^ TemperatureSeedMask));
                    long cooling = Math.Max(0, height - CoolingStart) * (long)IntNoise.One / CoolingBlocks;
                    long temperature = (gradient * GradientPercent + zones * (100 - GradientPercent)) / 100 - cooling;
                    temperatures[index] = Clamp(temperature);

                    // Rising ground makes the air rain: the slope facing the wind is wet, what lies beyond is dry.
                    long rain = 0;
                    int rise = Math.Min(height - previousHeight, MaxRise);
                    if (rise > 0)
                    {
                        rain = moisture * rise / RainDivisor;
                        moisture -= rain;
                    }

                    moisture += (IntNoise.One - moisture) / RechargeCells;
                    previousHeight = height;
                    airMoisture[index] = (int)moisture;
                    rainfall[index] = (int)rain;
                }
            }

            // Each row of cells is worked out on its own: blurring across rows hides the seams.
            airMoisture = Blur(airMoisture, AirBlurCellsX, AirBlurCellsZ);
            rainfall = Blur(rainfall, AirBlurCellsX, AirBlurCellsZ);

            for (int cellZ = 0; cellZ < CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < CellsX; cellX++)
                {
                    // The zones say how wet the land is; air that has lost its moisture makes it drier.
                    int index = CellIndex(cellX, cellZ);
                    long zoneHumidity = Contrast(IntNoise.Fractal(CentreOf(cellX), CentreOf(cellZ), ZoneCellShift, ZoneOctaves, seed ^ HumiditySeedMask));
                    long airShare = DryAirShare + (((IntNoise.One - DryAirShare) * (long)airMoisture[index]) >> 16);
                    long fromWater = ((long)waterNearby[index] * WaterHumidity) >> 16;
                    humidities[index] = Clamp(((zoneHumidity * airShare) >> 16) + rainfall[index] * 2L + fromWater);
                }
            }
        }

        // The mean height of four points of each cell, which follows the lie of the land better than a single column.
        private void FillHeights(Func<int, int, int> surfaceHeight)
        {
            const int near = CellSize / 4;
            const int far = CellSize - near;
            for (int cellZ = 0; cellZ < CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < CellsX; cellX++)
                {
                    int x = cellX << CellShift;
                    int z = cellZ << CellShift;
                    int sum = surfaceHeight(x + near, z + near) + surfaceHeight(x + far, z + near)
                        + surfaceHeight(x + near, z + far) + surfaceHeight(x + far, z + far);
                    heights[CellIndex(cellX, cellZ)] = sum / 4;
                }
            }
        }

        // Share of each cell under water, in fixed point, from the same four points as the heights.
        private int[] WaterShares(Func<int, int, bool> isWater)
        {
            var shares = new int[heights.Length];
            if (isWater == null) return shares;

            const int near = CellSize / 4;
            const int far = CellSize - near;
            for (int cellZ = 0; cellZ < CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < CellsX; cellX++)
                {
                    int x = cellX << CellShift;
                    int z = cellZ << CellShift;
                    int wet = (isWater(x + near, z + near) ? 1 : 0) + (isWater(x + far, z + near) ? 1 : 0)
                        + (isWater(x + near, z + far) ? 1 : 0) + (isWater(x + far, z + far) ? 1 : 0);
                    shares[CellIndex(cellX, cellZ)] = wet * (IntNoise.One / 4);
                }
            }

            return shares;
        }

        // Mean over a box of cells around each cell, cut at the world's edges.
        private int[] Blur(int[] values, int radiusX, int radiusZ)
        {
            var across = new int[values.Length];
            for (int cellZ = 0; cellZ < CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < CellsX; cellX++)
                {
                    long sum = 0;
                    int count = 0;
                    for (int other = Math.Max(0, cellX - radiusX); other <= Math.Min(CellsX - 1, cellX + radiusX); other++)
                    {
                        sum += values[CellIndex(other, cellZ)];
                        count++;
                    }

                    across[CellIndex(cellX, cellZ)] = (int)(sum / count);
                }
            }

            var result = new int[values.Length];
            for (int cellZ = 0; cellZ < CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < CellsX; cellX++)
                {
                    long sum = 0;
                    int count = 0;
                    for (int other = Math.Max(0, cellZ - radiusZ); other <= Math.Min(CellsZ - 1, cellZ + radiusZ); other++)
                    {
                        sum += across[CellIndex(cellX, other)];
                        count++;
                    }

                    result[CellIndex(cellX, cellZ)] = (int)(sum / count);
                }
            }

            return result;
        }

        private int Blend(int[] values, int x, int z)
        {
            Locate(x, CellsX, out int cellX, out long tx);
            Locate(z, CellsZ, out int cellZ, out long tz);

            long a = values[CellIndex(cellX, cellZ)];
            long b = values[CellIndex(cellX + 1, cellZ)];
            long c = values[CellIndex(cellX, cellZ + 1)];
            long d = values[CellIndex(cellX + 1, cellZ + 1)];

            long near = a + (((b - a) * tx) >> 16);
            long far = c + (((d - c) * tx) >> 16);
            return (int)(near + (((far - near) * tz) >> 16));
        }

        // The cell whose centre lies at or before the position, and how far the position is from
        // it towards the next centre, in fixed point. Beyond the outer centres the value is held.
        private static void Locate(int position, int cells, out int cell, out long weight)
        {
            int offset = position - CellSize / 2;
            cell = offset >> CellShift;
            weight = (long)(offset & (CellSize - 1)) * IntNoise.One / CellSize;

            if (cell < 0)
            {
                cell = 0;
                weight = 0;
            }
            else if (cell >= cells - 1)
            {
                cell = cells - 2;
                weight = IntNoise.One;
            }
        }

        // Fractal noise clusters around its middle: it is stretched away from it, so that the
        // zones reach real extremes.
        private static long Contrast(long value)
        {
            long half = IntNoise.One / 2;
            return Clamp(half + (value - half) * ContrastPercent / 100);
        }

        private static int CentreOf(int cell) => (cell << CellShift) + CellSize / 2;

        private static int Clamp(long value) => (int)Math.Min(Math.Max(value, 0), IntNoise.One - 1);

        private int CellIndex(int cellX, int cellZ) => cellX + CellsX * cellZ;
    }
}
