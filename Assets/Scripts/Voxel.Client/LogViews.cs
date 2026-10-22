using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // How logs lying on the ground look: a length of trunk in its tree's bark (two crossed boxes, so
    // it looks round enough), with pale cut ends. Drawn instanced, one batch per species of bark and
    // one for all the ends. A log is placed by the middle of its underside and the way it lies.
    internal sealed class LogViews
    {
        public const float Length = 0.92f;
        public const float Thickness = 0.3f;

        private const int MaxInstancesPerCall = 1023;

        private readonly Mesh bark;
        private readonly Mesh ends;
        private readonly RenderParams[] barkParams;
        private readonly RenderParams endParams;
        private readonly List<Matrix4x4>[] bySpecies;
        private readonly List<Matrix4x4> all = new List<Matrix4x4>();
        private Matrix4x4[] batch = new Matrix4x4[64];

        public LogViews(IReadOnlyList<SpeciesAsset> species, Material cut)
        {
            // Lying along +z, its underside at y = 0. No two faces in one plane.
            float half = Thickness * 0.5f;
            bark = BoxMesh.Build("Log bark", new[]
            {
                new[]
                {
                    (new Vector3(0f, half, 0f), new Vector3(Thickness, Thickness * 0.74f, Length)),
                    (new Vector3(0f, half, 0f), new Vector3(Thickness * 0.74f, Thickness, Length - 0.02f))
                }
            }, true);
            ends = BoxMesh.Build("Log ends", new[]
            {
                new[]
                {
                    (new Vector3(0f, half, Length * 0.5f + 0.004f), new Vector3(Thickness * 0.64f, Thickness * 0.64f, 0.016f)),
                    (new Vector3(0f, half, -Length * 0.5f - 0.004f), new Vector3(Thickness * 0.64f, Thickness * 0.64f, 0.016f))
                }
            }, true);

            barkParams = new RenderParams[species.Count];
            bySpecies = new List<Matrix4x4>[species.Count];
            for (int s = 0; s < species.Count; s++)
            {
                Material material = species[s].BarkMaterial != null ? species[s].BarkMaterial : cut;
                barkParams[s] = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
                bySpecies[s] = new List<Matrix4x4>();
            }

            endParams = new RenderParams(cut) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
        }

        // A log to draw this frame: the middle of its underside, the way it lies (radians about the
        // vertical, 0 along +z) and its tree's species.
        public void Add(Vector3 position, float yaw, int species)
        {
            if ((uint)species >= (uint)bySpecies.Length) return;

            Matrix4x4 matrix = Matrix4x4.TRS(position, Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f), Vector3.one);
            bySpecies[species].Add(matrix);
            all.Add(matrix);
        }

        // Draws the logs added since the last call.
        public void Render()
        {
            if (all.Count == 0) return;

            Bounds bounds = BoundsOf(all);
            for (int s = 0; s < bySpecies.Length; s++)
            {
                if (bySpecies[s].Count == 0) continue;

                Draw(barkParams[s], bark, bySpecies[s], bounds);
                bySpecies[s].Clear();
            }

            Draw(endParams, ends, all, bounds);
            all.Clear();
        }

        private void Draw(RenderParams parameters, Mesh mesh, List<Matrix4x4> matrices, Bounds bounds)
        {
            if (batch.Length < matrices.Count) batch = new Matrix4x4[Mathf.NextPowerOfTwo(matrices.Count)];
            matrices.CopyTo(batch);
            parameters.worldBounds = bounds;
            for (int start = 0; start < matrices.Count; start += MaxInstancesPerCall)
            {
                Graphics.RenderMeshInstanced(parameters, mesh, 0, batch, Mathf.Min(MaxInstancesPerCall, matrices.Count - start), start);
            }
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
