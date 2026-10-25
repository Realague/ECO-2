namespace Voxel.Core
{
    // How heavy each item is, in kg, for the carrying limit. Blocks and wood weigh nothing here: they
    // are counted apart (CarryLimits.MaxBlocks). A crop's weight comes from its plant species, a
    // carcass's from its animal species, both given by index.
    public sealed class ItemWeights
    {
        public const float Straw = 0.3f;
        public const float Seed = 0.01f;
        public const float Arrow = 0.05f;
        public const float Chest = 4f;
        public const float Stockpile = 2f;
        public const float SmallStockpile = 1f;
        public const float Torch = 0.1f;

        private readonly float[] produce;
        private readonly float[] carcasses;

        public ItemWeights(float[] produceWeights, float[] carcassWeights = null)
        {
            produce = produceWeights ?? new float[0];
            carcasses = carcassWeights ?? new float[0];
        }

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
            if (item == Blocks.Torch) return Torch;
            if (Items.IsSeed(item)) return Seed;
            switch (item)
            {
                case Items.Shovel: return 1f;
                case Items.Pickaxe: return 1.5f;
                case Items.Axe: return 1.2f;
                case Items.Scythe: return 1f;
                case Items.Hoe: return 0.8f;
                case Items.Bow: return 0.5f;
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

            return 0f;
        }
    }
}
