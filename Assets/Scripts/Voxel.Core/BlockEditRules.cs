using System;
using System.Numerics;

namespace Voxel.Core
{
    // Rules a block edit requested by a player must follow. The server applies them to every
    // request; they only depend on the world and on the position of the player's eyes.
    public static class BlockEditRules
    {
        // A player's position as known by the server lags a little behind the player's own.
        public const float ReachTolerance = 1.5f;

        public static bool IsAllowed(World world, Vector3 eyePosition, BlockPos pos, ushort block)
        {
            if (!world.InBounds(pos.X, pos.Y, pos.Z) || !Blocks.IsValid(block)) return false;

            // Placing requires an empty cell, breaking requires a block: a block is never replaced by another.
            bool placing = Blocks.IsSolid(block);
            bool occupied = Blocks.IsSolid(world.GetBlock(pos));
            if (placing == occupied) return false;

            float maxDistance = PlayerRules.Reach + ReachTolerance;
            return DistanceSquaredToBlock(eyePosition, pos) <= maxDistance * maxDistance;
        }

        private static float DistanceSquaredToBlock(Vector3 point, BlockPos block)
        {
            float dx = point.X - Math.Clamp(point.X, block.X, block.X + 1);
            float dy = point.Y - Math.Clamp(point.Y, block.Y, block.Y + 1);
            float dz = point.Z - Math.Clamp(point.Z, block.Z, block.Z + 1);
            return dx * dx + dy * dy + dz * dz;
        }
    }
}
