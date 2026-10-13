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

        // Building blocks: they do not exist in the terrain, players craft them.
        public const ushort Planks = 4;
        public const ushort Bricks = 5;
        public const ushort Timbered = 6;

        public const ushort LastType = Timbered;

        public static ushort TypeOf(ushort block) => (ushort)(block & TypeMask);

        public static int OrientationOf(ushort block) => block >> TypeBits;

        public static ushort WithOrientation(ushort type, int orientation) =>
            (ushort)(TypeOf(type) | (orientation << TypeBits));

        public static bool IsSolid(ushort block) => TypeOf(block) != Air;

        // A block received from the network is only accepted if its type exists. No type can be oriented yet.
        public static bool IsValid(ushort block) => TypeOf(block) <= LastType && OrientationOf(block) == 0;
    }
}
