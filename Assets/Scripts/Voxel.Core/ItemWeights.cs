namespace Voxel.Core
{
    // How heavy each item is, in kg, for the carrying limit. Blocks weigh nothing here: they are
    // counted apart (CarryLimits.MaxBlocks). A crop's and a wood's weight come from their plant species,
    // a carcass's from its animal species, all given by index.
    public sealed class ItemWeights
    {
        public const float Straw = 0.3f;
        public const float Seed = 0.01f;
        public const float Arrow = 0.05f;
        public const float Chest = 4f;
        public const float Stockpile = 2f;
        public const float SmallStockpile = 1f;
        public const float Campfire = 5f;
        public const float Torch = 0.1f;
        public const float Door = 3f;
        public const float CarpentryTable = 8f;
        public const float ButcherTable = 6f;
        public const float Meat = 0.3f;
        public const float Hide = 0.5f;
        public const float Bones = 0.2f;
        public const float Fat = 0.2f;

        // A lump of ore (Items.Ore): coal and salt are lighter, flint a nodule.
        public static float OreWeight(OreKind ore)
        {
            switch (ore)
            {
                case OreKind.Coal: return 0.5f;
                case OreKind.RockSalt: return 0.5f;
                case OreKind.Flint: return 0.3f;
                default: return 1f;
            }
        }

        // A wood of a species the table does not know.
        public const float DefaultWood = 0.5f;

        private readonly float[] produce;
        private readonly float[] carcasses;
        private readonly float[] woods;

        // What a player's skills take off the weight of wood (Felling's "Taille des stacks"), 1 for none.
        public float WoodFactor { get; }

        public ItemWeights(float[] produceWeights, float[] carcassWeights = null, float[] woodWeights = null, float woodFactor = 1f)
        {
            produce = produceWeights ?? new float[0];
            carcasses = carcassWeights ?? new float[0];
            woods = woodWeights ?? new float[0];
            WoodFactor = woodFactor;
        }

        // The same weights, with wood weighing this share of its own (a player's skills).
        public ItemWeights WithWoodFactor(float factor) => factor == WoodFactor ? this : new ItemWeights(produce, carcasses, woods, factor);

        // A carcass's weight by its animal's health (how big it is; AnimalSpeciesAsset.health: small
        // game 30, a fox or a heron 50, a deer or a wolf 60, a bear or a crocodile 100): 1 kg, 3, 6
        // and 10, so that a player can carry a few, and even the heaviest beside everything they are
        // given at first (tools, arrows, chest and stockpile: 13 kg).
        public const float HeaviestCarcass = 10f;

        public static float CarcassWeightFor(float health)
        {
            if (health <= 30f) return 1f;
            if (health <= 50f) return 3f;
            return health <= 60f ? 6f : HeaviestCarcass;
        }

        public float Of(int item)
        {
            if (item == Items.Straw) return Straw;
            if (item == Items.Arrow) return Arrow;
            if (item == Items.Chest) return Chest;
            if (item == Items.Stockpile) return Stockpile;
            if (item == Items.SmallStockpile) return SmallStockpile;
            if (item == Items.Campfire) return Campfire;
            if (item == Items.ButcherTable) return ButcherTable;
            if (Items.IsMeat(item)) return Meat;
            if (item == Items.Hide) return Hide;
            if (item == Items.Bones) return Bones;
            if (item == Items.Fat) return Fat;
            if (Items.IsOre(item)) return OreWeight(Items.OreKindOf(item));
            if (Dishes.TryGet(item, out Dish dish)) return dish.Weight;
            if (item == Blocks.Torch) return Torch;
            if (item == Blocks.Door) return Door;
            if (item == Items.CarpentryTable) return CarpentryTable;
            if (Items.IsSeed(item)) return Seed;
            switch (item)
            {
                case Items.Shovel: return 1f;
                case Items.Pickaxe: return 1.5f;
                case Items.Axe: return 1.2f;
                case Items.Scythe: return 1f;
                case Items.Hoe: return 0.8f;
                case Items.Bow: return 0.5f;
                case Items.Knife: return 0.3f;
                case Items.Hammer: return 1f;
            }

            if (Items.IsProduce(item))
            {
                int species = Items.ProduceSpecies(item);
                return species < produce.Length ? produce[species] : 0f;
            }

            if (Items.IsCarcass(item))
            {
                int species = Items.CarcassSpecies(item);
                return species < carcasses.Length ? carcasses[species] : CarcassWeightFor(60f);
            }

            if (Items.IsWood(item))
            {
                int species = Items.WoodSpecies(item);
                return (species < woods.Length && woods[species] > 0f ? woods[species] : DefaultWood) * WoodFactor;
            }

            return 0f;
        }
    }
}
