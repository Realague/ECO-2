using System;

namespace Eco.Sim
{
    // How many plants and animals of each species there are, and where: for the map's species
    // layers. Read only, with no random draw, and never called during a step, so it changes nothing
    // the simulation does.
    public sealed partial class Ecosystem
    {
        // The number of plants of each species, young ones included and stumps left out, in the order
        // of the species.
        public int[] PlantCounts()
        {
            CompactRemoved();
            var counts = new int[species.Length];
            for (int i = 0; i < plantCount; i++)
            {
                if (!plants[i].IsStump) counts[plants[i].SpeciesIndex]++;
            }

            return counts;
        }

        // The number of living animals of each species, in the order of the species.
        public int[] AnimalCounts()
        {
            var counts = new int[animalSpecies.Length];
            for (int i = 0; i < animalCount; i++)
            {
                if (animals[i].Age != DeadAge) counts[animals[i].SpeciesIndex]++;
            }

            return counts;
        }

        // How many plants of a species stand in each square of cellSize × cellSize columns: an array
        // of CensusCellsX(cellSize) × CensusCellsZ(cellSize) cells, x first. Empty for an unknown species.
        public int[] PlantDensity(int speciesIndex, int cellSize)
        {
            cellSize = Math.Max(1, cellSize);
            var cells = new int[CensusCellsX(cellSize) * CensusCellsZ(cellSize)];
            if ((uint)speciesIndex >= (uint)species.Length) return cells;

            CompactRemoved();
            int cellsX = CensusCellsX(cellSize);
            for (int i = 0; i < plantCount; i++)
            {
                if (plants[i].SpeciesIndex == speciesIndex && !plants[i].IsStump) cells[plants[i].X / cellSize + cellsX * (plants[i].Z / cellSize)]++;
            }

            return cells;
        }

        // The same for the living animals of a species.
        public int[] AnimalDensity(int speciesIndex, int cellSize)
        {
            cellSize = Math.Max(1, cellSize);
            var cells = new int[CensusCellsX(cellSize) * CensusCellsZ(cellSize)];
            if ((uint)speciesIndex >= (uint)animalSpecies.Length) return cells;

            int cellsX = CensusCellsX(cellSize);
            for (int i = 0; i < animalCount; i++)
            {
                Animal animal = animals[i];
                if (animal.SpeciesIndex == speciesIndex && animal.Age != DeadAge) cells[animal.X / cellSize + cellsX * (animal.Z / cellSize)]++;
            }

            return cells;
        }

        // The mean level of a nutrient in each square of cellSize × cellSize columns, laid out as the
        // densities are (the soil's own cells are CellSize wide: a census cell averages those it covers).
        public float[] SoilLevels(Nutrient nutrient, int cellSize)
        {
            cellSize = Math.Max(1, cellSize);
            int cellsX = CensusCellsX(cellSize);
            var sums = new float[cellsX * CensusCellsZ(cellSize)];
            var counts = new int[sums.Length];
            for (int cz = 0; cz < this.cellsZ; cz++)
            {
                for (int cx = 0; cx < this.cellsX; cx++)
                {
                    int target = cx * CellSize / cellSize + cellsX * (cz * CellSize / cellSize);
                    sums[target] += soil[(cx + this.cellsX * cz) * NutrientCount + (int)nutrient];
                    counts[target]++;
                }
            }

            for (int i = 0; i < sums.Length; i++)
            {
                if (counts[i] > 0) sums[i] /= counts[i];
            }

            return sums;
        }

        // The mean level of a nutrient over the whole world.
        public float MeanSoil(Nutrient nutrient)
        {
            int cells = soil.Length / NutrientCount;
            float sum = 0f;
            for (int cell = 0; cell < cells; cell++)
            {
                sum += soil[cell * NutrientCount + (int)nutrient];
            }

            return cells > 0 ? sum / cells : 0f;
        }

        // Cells across the world for a census: a world whose size is not a multiple of the cell
        // has a narrower last row.
        public int CensusCellsX(int cellSize) => (world.SizeX + Math.Max(1, cellSize) - 1) / Math.Max(1, cellSize);

        public int CensusCellsZ(int cellSize) => (world.SizeZ + Math.Max(1, cellSize) - 1) / Math.Max(1, cellSize);
    }
}
