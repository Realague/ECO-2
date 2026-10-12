using System;
using System.Numerics;

namespace Voxel.Core
{
    // Règles qu'une modification de bloc demandée par un joueur doit respecter. Le serveur les applique
    // à chaque demande ; elles ne dépendent que du monde et de la position des yeux du joueur.
    public static class BlockEditRules
    {
        // La position d'un joueur connue du serveur a un peu de retard sur celle du joueur lui-même.
        public const float ReachTolerance = 1.5f;

        public static bool IsAllowed(World world, Vector3 eyePosition, BlockPos pos, ushort block)
        {
            if (!world.InBounds(pos.X, pos.Y, pos.Z) || !Blocks.IsValid(block)) return false;

            // Poser exige une case vide, casser exige un bloc : on ne remplace pas un bloc par un autre.
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
