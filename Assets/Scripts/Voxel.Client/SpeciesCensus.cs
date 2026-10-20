using System;

namespace Voxel.Client
{
    // What the map's species layers show: how many of each species there are in the whole world,
    // and where the chosen one lives (EcosystemSync.RequestCensus). On a client the server sends
    // it, since a client only knows the animals near its player.
    public sealed class SpeciesCensus
    {
        public enum Kind : byte
        {
            None,
            Plant,
            Animal
        }

        // Cells of the density grid, in columns: those of the climate (Geography.CellSize).
        public const int CellSize = Voxel.Core.Geography.CellSize;

        public int[] PlantCounts = Array.Empty<int>();
        public int[] AnimalCounts = Array.Empty<int>();

        // The species the grid is for (None: no grid), and how many of it stand in each cell
        // (x first, CellsX × CellsZ cells, at most 255 each).
        public Kind GridKind;
        public int GridSpecies;
        public int CellsX;
        public int CellsZ;
        public byte[] Grid = Array.Empty<byte>();

        // Raised by one each time a census arrives, so that a screen redraws only then.
        public int Version;
    }
}
