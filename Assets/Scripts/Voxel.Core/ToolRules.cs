namespace Voxel.Core
{
    // Which tool each action needs (Items.Shovel...). Breaking a block needs a shovel for soft ground
    // and a pickaxe for anything hard; felling a plant an axe if it gives wood, else a scythe;
    // harvesting an annual crop a scythe, picking a perennial's fruit nothing; tilling a hoe; hunting
    // the bow. Items.None means bare hands do.
    public static class ToolRules
    {
        // The tool a block needs to be broken; Items.None for one that cannot be broken at all, or
        // for a torch, taken down by hand (Blocks.IsBreakable tells them apart).
        public static int ToBreak(ushort block)
        {
            if (!Blocks.IsBreakable(block)) return Items.None;

            switch (Blocks.TypeOf(block))
            {
                // Taken down by hand: whatever is in it does.
                case Blocks.Torch:
                    return Items.None;
                case Blocks.Dirt:
                case Blocks.Grass:
                case Blocks.Sand:
                case Blocks.Snow:
                case Blocks.Gravel:
                case Blocks.Clay:
                case Blocks.Mud:
                case Blocks.TilledSoil:
                    return Items.Shovel;
                default:
                    return Items.Pickaxe;
            }
        }

        // Ground a hoe turns into tilled soil.
        public static bool CanTill(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            return type == Blocks.Grass || type == Blocks.Dirt || type == Blocks.Mud || type == Blocks.Clay;
        }

        public static int ToFell(bool givesWood) => givesWood ? Items.Axe : Items.Scythe;

        public static int ToHarvest(bool annual) => annual ? Items.Scythe : Items.None;
    }
}
