using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // How arrows look: a shaft, an iron head and a fletching, built as boxes at start, each drawn
    // with its own instanced material. An arrow is placed by the point of its head and the way it
    // points, flying or stuck where it landed.
    internal sealed class ArrowViews
    {
        public const float Length = 0.8f;

        private const int MaxInstancesPerCall = 1023;

        private readonly Mesh[] meshes;
        private readonly RenderParams[] renderParams;
        private readonly List<Matrix4x4> matrices = new List<Matrix4x4>();
        private Matrix4x4[] batch = new Matrix4x4[64];

        public ArrowViews(Material shaft, Material head, Material fletching)
        {
            // The point is at the origin, the arrow lying along -z behind it.
            meshes = new[]
            {
                Boxes(new[] { (new Vector3(0f, 0f, -0.44f), new Vector3(0.035f, 0.035f, 0.72f)) }),
                Boxes(new[] { (new Vector3(0f, 0f, -0.05f), new Vector3(0.07f, 0.02f, 0.1f)), (new Vector3(0f, 0f, -0.07f), new Vector3(0.02f, 0.07f, 0.06f)) }),
                Boxes(new[] { (new Vector3(0f, 0f, -0.71f), new Vector3(0.15f, 0.008f, 0.13f)), (new Vector3(0f, 0f, -0.712f), new Vector3(0.008f, 0.15f, 0.13f)) })
            };

            Mesh Boxes((Vector3, Vector3)[] boxes) => BoxMesh.Build("Arrow part", new[] { boxes });

            Material[] materials = { shaft, head, fletching };
            renderParams = new RenderParams[materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                renderParams[i] = new RenderParams(materials[i]) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            }
        }

        // An arrow to draw this frame, by the point of its head and the way it points.
        public void Add(Vector3 point, Vector3 direction)
        {
            if (direction.sqrMagnitude < 1e-8f) direction = Vector3.forward;
            matrices.Add(Matrix4x4.TRS(point, Quaternion.LookRotation(direction), Vector3.one));
        }

        // The meshes of one arrow, placed as Add would draw it (for PickupHalo).
        public void CollectParts(Vector3 point, Vector3 direction, List<(Mesh mesh, Matrix4x4 matrix)> into)
        {
            if (direction.sqrMagnitude < 1e-8f) direction = Vector3.forward;
            Matrix4x4 placed = Matrix4x4.TRS(point, Quaternion.LookRotation(direction), Vector3.one);
            foreach (Mesh mesh in meshes) into.Add((mesh, placed));
        }

        // Draws the arrows added since the last call.
        public void Render()
        {
            if (matrices.Count == 0) return;

            if (batch.Length < matrices.Count) batch = new Matrix4x4[Mathf.NextPowerOfTwo(matrices.Count)];
            matrices.CopyTo(batch);
            Bounds bounds = BoundsOf(matrices);
            for (int part = 0; part < meshes.Length; part++)
            {
                RenderParams parameters = renderParams[part];
                parameters.worldBounds = bounds;
                for (int start = 0; start < matrices.Count; start += MaxInstancesPerCall)
                {
                    Graphics.RenderMeshInstanced(parameters, meshes[part], 0, batch, Mathf.Min(MaxInstancesPerCall, matrices.Count - start), start);
                }
            }

            matrices.Clear();
        }

        private static Bounds BoundsOf(List<Matrix4x4> placed)
        {
            var bounds = new Bounds(placed[0].GetPosition(), Vector3.one * Length * 2f);
            for (int i = 1; i < placed.Count; i++)
            {
                bounds.Encapsulate(new Bounds(placed[i].GetPosition(), Vector3.one * Length * 2f));
            }

            return bounds;
        }
    }
}
