namespace Voxel.Core
{
    // An inventory item is designated by an integer. Blocks keep their type number; other
    // items use reserved ranges above.
    public static class Items
    {
        public const int None = 0;
        // The wood of every species was one item before each tree gave its own (WoodOf): saves holding
        // it load it as oak wood (Upgrade).
        public const int LegacyWood = 100;
        public const int Straw = 101;

        // Shot with the bow; a shot arrow sticks where it lands and can be picked up again.
        public const int Arrow = 102;

        // Storage, placed in the world (StorageRules): a chest holds items by weight, a stockpile
        // piles of blocks. Each player gets one of each at their first connection.
        public const int Chest = 103;
        public const int Stockpile = 104;

        public static bool IsStorage(int item) => item == Chest || item == Stockpile;

        // Tools: each player gets one of each at their first connection (there is no crafting), and
        // breaking, felling, harvesting annuals, tilling and hunting each need theirs (ToolRules).
        public const int Shovel = 110;
        public const int Pickaxe = 111;
        public const int Axe = 112;
        public const int Scythe = 113;
        public const int Hoe = 114;
        public const int Bow = 115;
        private const int FirstTool = Shovel;
        private const int LastTool = Bow;

        public static readonly int[] Tools = { Shovel, Pickaxe, Axe, Scythe, Hoe, Bow };

        // One seed per plant species: FirstSeed + index of the species.
        public const int MaxSeedSpecies = 100;
        private const int FirstSeed = 200;

        // One crop per plant species, what its harvest gives (an apple, a carrot): FirstProduce + index
        // of the species. Its name is the species' (SpeciesAsset.ProduceName), which only the ecosystem knows.
        public const int MaxProduceSpecies = 100;
        private const int FirstProduce = 300;

        // One wood per plant species that gives wood, what its logs give: FirstWood + index of the species,
        // named after it ("Oak wood"). Oak (index 0) takes the wood of saves from before.
        public const int MaxWoodSpecies = 100;
        private const int FirstWood = 400;
        public const int OakSpecies = 0;

        // One carcass per animal species, what a player picks up where an animal died: FirstCarcass +
        // index of the animal species, named after it ("Deer carcass"). Not yet of any use (butchering will come).
        public const int MaxCarcassSpecies = 100;
        private const int FirstCarcass = 500;

        public static int OfBlock(ushort block) => Blocks.TypeOf(block);

        public static bool IsBlock(int item) => item > Blocks.Air && item <= Blocks.LastType && !Blocks.IsRetired((ushort)item);

        // An item of an earlier version that no longer exists (the crafted blocks): saved
        // inventories lose it instead of being refused.
        public static bool IsRetired(int item) => item > Blocks.Air && item < LegacyWood && Blocks.IsRetired((ushort)item);

        public static ushort BlockOf(int item) => (ushort)item;

        public static int Seed(int plantSpecies) => FirstSeed + plantSpecies;

        public static bool IsSeed(int item) => item >= FirstSeed && item < FirstSeed + MaxSeedSpecies;

        public static int SeedSpecies(int item) => item - FirstSeed;

        public static int Produce(int plantSpecies) => FirstProduce + plantSpecies;

        public static bool IsProduce(int item) => item >= FirstProduce && item < FirstProduce + MaxProduceSpecies;

        public static int ProduceSpecies(int item) => item - FirstProduce;

        public static int WoodOf(int plantSpecies) => FirstWood + plantSpecies;

        public static bool IsWood(int item) => item >= FirstWood && item < FirstWood + MaxWoodSpecies;

        public static int WoodSpecies(int item) => item - FirstWood;

        public static int Carcass(int animalSpecies) => FirstCarcass + animalSpecies;

        public static bool IsCarcass(int item) => item >= FirstCarcass && item < FirstCarcass + MaxCarcassSpecies;

        public static int CarcassSpecies(int item) => item - FirstCarcass;

        // What an item read from a save of an earlier version is now (the one wood became oak wood).
        public static int Upgrade(int item) => item == LegacyWood ? WoodOf(OakSpecies) : item;

        public static bool IsTool(int item) => item >= FirstTool && item <= LastTool;

        public static bool IsValid(int item) =>
            IsBlock(item) || IsWood(item) || item == Straw || item == Arrow || IsStorage(item) || IsTool(item) || IsSeed(item) || IsProduce(item) || IsCarcass(item);

        // What a broken block yields. Grass and tilled soil cannot be picked up: they give dirt.
        public static int DropOf(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type == Blocks.Grass || type == Blocks.TilledSoil ? Blocks.Dirt : type;
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
                case Blocks.TilledSoil: return "Tilled soil";
                case Straw: return "Straw";
                case Arrow: return "Arrow";
                case Chest: return "Chest";
                case Stockpile: return "Stockpile";
                case Shovel: return "Shovel";
                case Pickaxe: return "Pickaxe";
                case Axe: return "Axe";
                case Scythe: return "Scythe";
                case Hoe: return "Hoe";
                case Bow: return "Bow";
                default: return IsSeed(item) ? "Seed" : IsProduce(item) ? "Crop" : IsWood(item) ? "Wood" : IsCarcass(item) ? "Carcass" : "Unknown item";
            }
        }
    }
}
