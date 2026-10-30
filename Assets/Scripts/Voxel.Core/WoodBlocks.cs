using System.Collections.Generic;

namespace Voxel.Core
{
    // The building blocks made of wood, one planks and one log per plant species that gives wood, in
    // the order of Species: planks Blocks.FirstPlanks + n, logs Blocks.FirstLog + n. Species are named by
    // their index in EcosystemSync.speciesAssets (reordering species already breaks saves), checked
    // against the asset names when EcosystemSync wakes.
    public static class WoodBlocks
    {
        public const int Count = 19;

        public static readonly IReadOnlyList<(int Index, string Asset)> Species = new[]
        {
            (0, "Oak"), (2, "Birch"), (3, "Pine"), (4, "Acacia"), (5, "Poplar"), (8, "Cactus"), (10, "JungleTree"),
            (11, "Palm"), (13, "Willow"), (14, "Cypress"), (33, "AppleTree"), (34, "CherryTree"), (35, "LemonTree"),
            (36, "OrangeTree"), (37, "BananaTree"), (38, "CacaoTree"), (39, "CoffeeShrub"), (40, "MangoTree"), (41, "DatePalm")
        };

        public static bool IsPlanks(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type >= Blocks.FirstPlanks && type < Blocks.FirstPlanks + Count;
        }

        public static bool IsLog(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type >= Blocks.FirstLog && type < Blocks.FirstLog + Count;
        }

        public static bool IsWoodBlock(ushort block) => IsPlanks(block) || IsLog(block);

        // The wood a planks or log block is made of: its place in Species.
        public static int WoodOf(ushort block) => Blocks.TypeOf(block) - (IsLog(block) ? Blocks.FirstLog : Blocks.FirstPlanks);

        // The plant species (its index in EcosystemSync.speciesAssets) a planks or log block is made of.
        public static int SpeciesOf(ushort block) => Species[WoodOf(block)].Index;

        // The planks or log of a plant species, if it gives wood (else false).
        public static bool TryPlanksOf(int species, out ushort planks) => TryBlockOf(species, Blocks.FirstPlanks, out planks);

        public static bool TryLogOf(int species, out ushort log) => TryBlockOf(species, Blocks.FirstLog, out log);

        private static bool TryBlockOf(int species, ushort first, out ushort block)
        {
            for (int n = 0; n < Count; n++)
            {
                if (Species[n].Index != species) continue;

                block = (ushort)(first + n);
                return true;
            }

            block = Blocks.Air;
            return false;
        }
    }
}
