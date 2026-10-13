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

        // Blocs de construction : ils n'existent pas dans le terrain, les joueurs les fabriquent.
        public const ushort Planks = 4;
        public const ushort Bricks = 5;
        public const ushort Timbered = 6;

        public const ushort LastType = Timbered;

        public static ushort TypeOf(ushort block) => (ushort)(block & TypeMask);

        public static int OrientationOf(ushort block) => block >> TypeBits;

        public static ushort WithOrientation(ushort type, int orientation) =>
            (ushort)(TypeOf(type) | (orientation << TypeBits));

        public static bool IsSolid(ushort block) => TypeOf(block) != Air;

        // Un bloc reçu du réseau n'est accepté que si son type existe. Aucun type n'est encore orientable.
        public static bool IsValid(ushort block) => TypeOf(block) <= LastType && OrientationOf(block) == 0;
    }
}
