using System;

namespace Voxel.Client
{
    // What the map's species and soil layers show: how many of each species there are in the whole
    // world, the soil's mean nutrients, and a grid for the chosen layer: where a species lives, or
    // how rich the soil is in one nutrient (EcosystemSync.RequestCensus). On a client the server
    // sends it, since a client only knows the animals near its player and nothing of the soil.
    public sealed class SpeciesCensus
    {
        public enum Kind : byte
        {
            None,
            Plant,
            Animal,

            // The soil: GridSpecies is the nutrient (Eco.Sim.Nutrient).
            Soil
        }

        // Cells of the grid, in columns: those of the climate (Geography.CellSize).
        public const int CellSize = Voxel.Core.Geography.CellSize;

        // A soil grid's byte is the nutrient's level times this (a level of 2, over capacity, is 255).
        public const float SoilLevelScale = 127.5f;

        public int[] PlantCounts = Array.Empty<int>();
        public int[] AnimalCounts = Array.Empty<int>();

        // The mean level of nitrogen, phosphorus and potassium over the world.
        public float[] SoilMeans = new float[3];

        // The layer the grid is for (None: no grid): how many of a species stand in each cell (at
        // most 255), or a nutrient's level (times SoilLevelScale); x first, CellsX × CellsZ cells.
        public Kind GridKind;
        public int GridSpecies;
        public int CellsX;
        public int CellsZ;
        public byte[] Grid = Array.Empty<byte>();

        // Raised by one each time a census arrives, so that a screen redraws only then.
        public int Version;
    }
}
