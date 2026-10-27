namespace Voxel.Core
{
    // What a ripe crop is harvested with, set per species: by hand (fruit, leafy and bean crops,
    // mushrooms), a scythe (cereals and sunflowers) or a shovel (roots, dug up).
    public enum HarvestTool : byte
    {
        Hand,
        Scythe,
        Shovel
    }

    // Which tool each action needs (Items.Shovel...). Breaking a block needs a shovel for soft ground
    // and a pickaxe for anything hard; felling a plant an axe if it gives wood, a shovel for a root
    // crop, else a scythe; harvesting a crop its species' HarvestTool; tilling a hoe; hunting the bow.
    // Items.None means bare hands do.
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

        // A plant giving wood is felled with an axe, a root crop dug up with a shovel, anything else cut
        // with a scythe.
        public static int ToFell(bool givesWood, HarvestTool harvestTool = HarvestTool.Hand)
        {
            if (givesWood) return Items.Axe;
            return harvestTool == HarvestTool.Shovel ? Items.Shovel : Items.Scythe;
        }

        // The tool a ripe crop is harvested with (Items.None: by hand).
        public static int ToHarvest(HarvestTool harvestTool)
        {
            switch (harvestTool)
            {
                case HarvestTool.Scythe: return Items.Scythe;
                case HarvestTool.Shovel: return Items.Shovel;
                default: return Items.None;
            }
        }
    }
}
