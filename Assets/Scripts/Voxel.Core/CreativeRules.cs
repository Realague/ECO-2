namespace Voxel.Core
{
    // Creative mode's catalog: how many of an item a click takes (a right click takes one), and the
    // most a single request may ask for.
    public static class CreativeRules
    {
        public const int Stack = 20;
        public const int MaxPerRequest = 999;

        // One of a tool or of a storage (a chest, a stockpile, a station), a stack of anything else.
        public static int StackOf(int item) => Items.IsTool(item) || Items.IsStorage(item) ? 1 : Stack;
    }
}
