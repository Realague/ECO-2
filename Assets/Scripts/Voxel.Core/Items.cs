namespace Voxel.Core
{
    // An inventory item is designated by an integer. Blocks keep their type number; other
    // items use reserved ranges above.
    public static class Items
    {
        public const int None = 0;
        public const int Wood = 100;
        public const int Straw = 101;

        // One seed per plant species: FirstSeed + index of the species.
        public const int MaxSeedSpecies = 100;
        private const int FirstSeed = 200;

        // One crop per plant species, what its harvest gives (an apple, a carrot): FirstProduce + index
        // of the species. Its name is the species' (SpeciesAsset.ProduceName), which only the ecosystem knows.
        public const int MaxProduceSpecies = 100;
        private const int FirstProduce = 300;

        public static int OfBlock(ushort block) => Blocks.TypeOf(block);

        public static bool IsBlock(int item) => item > Blocks.Air && item <= Blocks.LastType && !Blocks.IsRetired((ushort)item);

        // An item of an earlier version that no longer exists (the crafted blocks): saved
        // inventories lose it instead of being refused.
        public static bool IsRetired(int item) => item > Blocks.Air && item < Wood && Blocks.IsRetired((ushort)item);

        public static ushort BlockOf(int item) => (ushort)item;

        public static int Seed(int plantSpecies) => FirstSeed + plantSpecies;

        public static bool IsSeed(int item) => item >= FirstSeed && item < FirstSeed + MaxSeedSpecies;

        public static int SeedSpecies(int item) => item - FirstSeed;

        public static int Produce(int plantSpecies) => FirstProduce + plantSpecies;

        public static bool IsProduce(int item) => item >= FirstProduce && item < FirstProduce + MaxProduceSpecies;

        public static int ProduceSpecies(int item) => item - FirstProduce;

        public static bool IsValid(int item) => IsBlock(item) || item == Wood || item == Straw || IsSeed(item) || IsProduce(item);

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
                case Blocks.Granite: return "Granite";
                case Blocks.Dirt: return "Dirt";
                case Blocks.Grass: return "Grass";
                case Blocks.Water: return "Water";
                case Blocks.Ice: return "Ice";
                case Blocks.Basalt: return "Basalt";
                case Blocks.Limestone: return "Limestone";
                case Blocks.Marble: return "Marble";
                case Blocks.Sandstone: return "Sandstone";
                case Blocks.Slate: return "Slate";
                case Blocks.Sand: return "Sand";
                case Blocks.Snow: return "Snow";
                case Blocks.Gravel: return "Gravel";
                case Blocks.Clay: return "Clay";
                case Blocks.CoalOre: return "Coal ore";
                case Blocks.IronOre: return "Iron ore";
                case Blocks.CopperOre: return "Copper ore";
                case Blocks.TinOre: return "Tin ore";
                case Blocks.GoldOre: return "Gold ore";
                case Blocks.RockSalt: return "Rock salt";
                case Blocks.Flint: return "Flint";
                case Blocks.Bedrock: return "Bedrock";
                case Blocks.Mud: return "Mud";
                case Wood: return "Wood";
                case Straw: return "Straw";
                default: return IsSeed(item) ? "Seed" : IsProduce(item) ? "Crop" : "Unknown item";
            }
        }
    }
}
