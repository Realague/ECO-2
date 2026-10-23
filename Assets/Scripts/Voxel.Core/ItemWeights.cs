namespace Voxel.Core
{
    // How heavy each item is, in kg, for the carrying limit. Blocks and wood weigh nothing here: they
    // are counted apart (CarryLimits.MaxBlocks). A crop's weight comes from its species, given by index.
    public sealed class ItemWeights
    {
        public const float Straw = 0.3f;
        public const float Seed = 0.01f;
        public const float Arrow = 0.05f;
        public const float Chest = 4f;
        public const float Stockpile = 2f;

        private readonly float[] produce;

        public ItemWeights(float[] produceWeights)
        {
            produce = produceWeights ?? new float[0];
        }

        public float Of(int item)
        {
            if (item == Items.Straw) return Straw;
            if (item == Items.Arrow) return Arrow;
            if (item == Items.Chest) return Chest;
            if (item == Items.Stockpile) return Stockpile;
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

            return 0f;
        }
    }
}
