namespace Voxel.Core
{
    // The ores a rock may hold (OreKind), in the order of their overlays (BlockAtlas.OverlayNames).
    public enum OreKind : byte
    {
        None,
        Coal,
        Iron,
        Copper,
        Tin,
        Gold,
        RockSalt,
        Flint
    }

    // Ores are not blocks: a block of rock (granite, basalt, limestone, marble, sandstone, slate)
    // may hold one, kept in its variant bits (Blocks.VariantOf: the OreKind, 0 for none). Broken with
    // a pickaxe, it gives its rock, as any rock does, and the ore as an item of its own (Items.Ore),
    // Yield of them, into the breaker's bag together. It is drawn as its rock with the ore's overlay
    // over it (ChunkMesher), and is as hard as the harder of the two (MiningRules).
    //
    // Block types 18 to 24 were the ores as blocks of their own (in this OreKind order), some saved
    // with their host rock in their variant bits (granite, basalt, limestone, marble, sandstone,
    // slate, from 0): a save's block of those types is read as that rock holding the ore
    // (UpgradeBlock), and an item of those numbers as the ore (Items.Upgrade).
    public static class Ores
    {
        public const int Count = 7;

        public const ushort FirstFormerBlock = 18;
        public const ushort LastFormerBlock = FirstFormerBlock + Count - 1;

        // The rocks the former ore blocks kept in their variant bits, by code.
        private static readonly ushort[] FormerHosts =
        {
            Blocks.Granite, Blocks.Basalt, Blocks.Limestone, Blocks.Marble, Blocks.Sandstone, Blocks.Slate
        };

        // A rock, which may hold an ore.
        public static bool CanHold(ushort block)
        {
            switch (Blocks.TypeOf(block))
            {
                case Blocks.Granite:
                case Blocks.Basalt:
                case Blocks.Limestone:
                case Blocks.Marble:
                case Blocks.Sandstone:
                case Blocks.Slate:
                    return true;
                default:
                    return false;
            }
        }

        public static OreKind OreOf(ushort block) => CanHold(block) ? (OreKind)Blocks.VariantOf(block) : OreKind.None;

        public static bool HasOre(ushort block) => OreOf(block) != OreKind.None;

        // This rock holding this ore (none: the rock alone).
        public static ushort Make(ushort rock, OreKind ore) => Blocks.WithVariant(rock, (int)ore, 0);

        // The variant bits a rock may have: no ore, or one of them.
        public static bool IsValidCode(ushort block) => Blocks.VariantOf(block) <= Count && Blocks.OrientationOf(block) == 0;

        // How many of its ore breaking such a block gives. One for now: the mining skill will set it.
        public static int Yield(OreKind ore) => ore == OreKind.None ? 0 : 1;

        // How many hits the ore alone takes (MiningRules): a rock holding it takes the more of the two.
        public static int Hardness(OreKind ore)
        {
            switch (ore)
            {
                case OreKind.None: return 0;
                case OreKind.RockSalt: return 3;
                case OreKind.Flint: return 4;
                case OreKind.Gold: return 6;
                default: return 5;
            }
        }

        public static string NameOf(OreKind ore)
        {
            switch (ore)
            {
                case OreKind.Coal: return "Coal";
                case OreKind.Iron: return "Iron ore";
                case OreKind.Copper: return "Copper ore";
                case OreKind.Tin: return "Tin ore";
                case OreKind.Gold: return "Gold ore";
                case OreKind.RockSalt: return "Rock salt";
                case OreKind.Flint: return "Flint";
                default: return "";
            }
        }

        public static bool IsFormerBlock(int type) => type >= FirstFormerBlock && type <= LastFormerBlock;

        // A block read from a save: a former ore block becomes its rock holding the ore.
        public static ushort UpgradeBlock(ushort block)
        {
            ushort type = Blocks.TypeOf(block);
            if (!IsFormerBlock(type)) return block;

            int host = Blocks.VariantOf(block);
            return Make(host < FormerHosts.Length ? FormerHosts[host] : Blocks.Granite, (OreKind)(type - FirstFormerBlock + 1));
        }
    }
}
