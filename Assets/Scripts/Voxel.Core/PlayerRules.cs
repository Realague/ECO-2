namespace Voxel.Core
{
    // Size and reach of the player, shared between its local simulation and the server checks.
    public static class PlayerRules
    {
        public const float HalfWidth = 0.3f;
        public const float Height = 1.8f;
        public const float EyeHeight = 1.62f;
        public const float Reach = 5f;

        // How high the player steps up by walking, without jumping (VoxelCollision.Move): a whole block,
        // a little more so that it is reached from ground slightly lower; never a block and a half
        // (a block with a slab on it, a fence).
        public const float StepHeight = 1.05f;
    }
}
