namespace Voxel.Core
{
    // An inventory item is designated by an integer. Blocks keep their type number; other
    // items use reserved ranges above.
    public static class Items
    {
        public const int None = 0;
        public const int Wood = 100;

        // One seed per plant species: FirstSeed + index of the species.
        public const int MaxSeedSpecies = 100;
        private const int FirstSeed = 200;

        public static int OfBlock(ushort block) => Blocks.TypeOf(block);

        public static bool IsBlock(int item) => item > Blocks.Air && item <= Blocks.LastType;

        public static ushort BlockOf(int item) => (ushort)item;

        public static int Seed(int plantSpecies) => FirstSeed + plantSpecies;

        public static bool IsSeed(int item) => item >= FirstSeed && item < FirstSeed + MaxSeedSpecies;

        public static int SeedSpecies(int item) => item - FirstSeed;

        public static bool IsValid(int item) => IsBlock(item) || item == Wood || IsSeed(item);

        // What a broken block yields. Grass cannot be picked up: it gives dirt.
        public static int DropOf(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type == Blocks.Grass ? Blocks.Dirt : type;
        }

        // Display name. A seed also carries the name of its species, which only the ecosystem knows.
        public static string NameOf(int item)
        {
            switch (item)
            {
                case Blocks.Stone: return "Stone";
                case Blocks.Dirt: return "Dirt";
                case Blocks.Grass: return "Grass";
                case Blocks.Planks: return "Planks";
                case Blocks.Bricks: return "Bricks";
                case Blocks.Timbered: return "Timber frame";
                case Wood: return "Wood";
                default: return IsSeed(item) ? "Seed" : "Unknown item";
            }
        }
    }
}
