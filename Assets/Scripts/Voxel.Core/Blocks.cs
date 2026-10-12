namespace Voxel.Core
{
    // Un bloc tient sur deux octets : 12 bits de type (poids faibles), 4 bits d'orientation.
    public static class Blocks
    {
        public const int TypeBits = 12;
        public const ushort TypeMask = (1 << TypeBits) - 1;

        public const ushort Air = 0;
        public const ushort Stone = 1;
        public const ushort Dirt = 2;
        public const ushort Grass = 3;

        public static ushort TypeOf(ushort block) => (ushort)(block & TypeMask);

        public static int OrientationOf(ushort block) => block >> TypeBits;

        public static ushort WithOrientation(ushort type, int orientation) =>
            (ushort)(TypeOf(type) | (orientation << TypeBits));

        public static bool IsSolid(ushort block) => TypeOf(block) != Air;
    }
}
