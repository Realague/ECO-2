namespace Voxel.Core
{
    // A block fits in two bytes: 12 bits of type (low bits), 4 bits of orientation.
    public static class Blocks
    {
        public const int TypeBits = 12;
        public const ushort TypeMask = (1 << TypeBits) - 1;

        public const ushort Air = 0;
        public const ushort Stone = 1;
        public const ushort Dirt = 2;
        public const ushort Grass = 3;

        // Types 4 to 6 (planks, bricks, timber frame) and 26 to 31 (cut rocks) were crafted blocks,
        // removed with crafting: they are never reused, and found in a save they become air (see IsRetired).

        // Water fills lakes, rivers and the sea; ice covers them where it is cold.
        public const ushort Water = 7;
        public const ushort Ice = 8;

        // Rocks. Stone is granite: the core of the massifs and the deep basement everywhere.
        public const ushort Granite = Stone;
        public const ushort Basalt = 9;
        public const ushort Limestone = 10;
        public const ushort Marble = 11;
        public const ushort Sandstone = 12;
        public const ushort Slate = 13;

        // Loose ground: beaches and deserts, cold heights, river beds, lake beds.
        public const ushort Sand = 14;
        public const ushort Snow = 15;
        public const ushort Gravel = 16;
        public const ushort Clay = 17;

        // Ores and other resources found in the rock.
        public const ushort CoalOre = 18;
        public const ushort IronOre = 19;
        public const ushort CopperOre = 20;
        public const ushort TinOre = 21;
        public const ushort GoldOre = 22;
        public const ushort RockSalt = 23;
        public const ushort Flint = 24;

        // The bottom of the world: it cannot be broken.
        public const ushort Bedrock = 25;

        // The wet ground of marshes: the bed of their pools and what lies under their grass.
        public const ushort Mud = 32;

        // Soil a player has worked with a hoe: the only ground where they can plant seeds. Never
        // generated; broken, it gives dirt.
        public const ushort TilledSoil = 33;

        // A torch, placed by a player: it gives light (BlockLight). It stands on the top of a block
        // or hangs on the side of one (its orientation, Torches), lets everything through (players,
        // rays, light) and is broken by hand. Never generated.
        public const ushort Torch = 34;

        public const ushort LastType = Torch;

        // The light a torch gives to its own cell (BlockLight): it falls by one level per block.
        public const int TorchLight = 14;

        // Types that existed in earlier versions and no longer do: two gaps.
        private const ushort FirstRetiredGap = 4;
        private const ushort LastRetiredGap = 6;
        private const ushort FirstLaterRetired = 26;
        private const ushort LastLaterRetired = 31;

        public static ushort TypeOf(ushort block) => (ushort)(block & TypeMask);

        public static int OrientationOf(ushort block) => block >> TypeBits;

        public static ushort WithOrientation(ushort type, int orientation) =>
            (ushort)(TypeOf(type) | (orientation << TypeBits));

        // Collision, support and aiming: players and animals stand on solid blocks and cannot enter
        // them, and a ray stops at the first one. Water and torches are not solid.
        public static bool IsSolid(ushort block)
        {
            ushort type = TypeOf(block);
            return type != Air && type != Water && type != Torch;
        }

        public static bool IsTorch(ushort block) => TypeOf(block) == Torch;

        // What a player may set in a cell: any solid block, or a torch.
        public static bool IsPlaceable(ushort block) => IsSolid(block) || IsTorch(block);

        // Takes up its cell, as far as a column's height goes (World.GetTopHeight): solid blocks and water.
        public static bool FillsColumn(ushort block) => IsSolid(block) || IsWater(block);

        // The light a block gives off (BlockLight), 0 for most.
        public static int LightOf(ushort block) => IsTorch(block) ? TorchLight : 0;

        // Hides what is behind it: the faces of blocks touching an opaque block are not drawn. Water
        // is see-through, so the bed of a lake is drawn under it.
        public static bool IsOpaque(ushort block) => IsSolid(block);

        public static bool IsWater(ushort block) => TypeOf(block) == Water;

        // A block players can break: any solid block but the bedrock, and torches.
        public static bool IsBreakable(ushort block) => (IsSolid(block) && TypeOf(block) != Bedrock) || IsTorch(block);

        // Where a placed block may go: empty space, or water, which it pushes out.
        public static bool IsReplaceable(ushort block)
        {
            ushort type = TypeOf(block);
            return type == Air || type == Water;
        }

        // A block received from the network is only accepted if its type exists. Only torches are
        // oriented (Torches.Standing, or the side they hang on).
        public static bool IsValid(ushort block) =>
            TypeOf(block) <= LastType && !IsRetired(block) && OrientationOf(block) <= (IsTorch(block) ? Torches.LastOrientation : 0);

        // A type of an earlier version that no longer exists (the crafted blocks): saves turn it into air.
        public static bool IsRetired(ushort block)
        {
            ushort type = TypeOf(block);
            return (type >= FirstRetiredGap && type <= LastRetiredGap) || (type >= FirstLaterRetired && type <= LastLaterRetired);
        }
    }
}
