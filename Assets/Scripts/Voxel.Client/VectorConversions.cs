using UnityEngine;

namespace Voxel.Client
{
    // Voxel.Core n'utilise pas UnityEngine : ses vecteurs sont ceux de System.Numerics.
    internal static class VectorConversions
    {
        public static System.Numerics.Vector3 ToNumerics(this Vector3 v) => new System.Numerics.Vector3(v.x, v.y, v.z);

        public static Vector3 ToUnity(this System.Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
