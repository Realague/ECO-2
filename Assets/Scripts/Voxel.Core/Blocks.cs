namespace Voxel.Core
{
    public static class Blocks
    {
        public const byte Air = 0;
        public const byte Stone = 1;
        public const byte Dirt = 2;
        public const byte Grass = 3;

        public static bool IsSolid(byte block) => block != Air;
    }
}
