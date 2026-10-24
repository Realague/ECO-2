using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // How torches look (Blocks.Torch, which the chunk meshes leave out): a wooden stick with a dark
    // head and a flame that flickers, standing in the middle of its cell or leaning out from the
    // block it hangs on (Torches). Drawn instanced, three parts with a material each, for the torches
    // within ViewDistance of the camera; where they stand comes from BlockLight.Sources.
    internal sealed class TorchViews
    {
        private const float ViewDistance = 96f;
        private const int MaxInstancesPerCall = 1023;

        // A hanging torch's foot, from the middle of its cell toward its support and up, and how far it leans.
        private const float WallInset = 0.36f;
        private const float WallFoot = 0.18f;
        private const float WallLean = 0.45f;

        private static readonly Vector3 FlameCentre = new Vector3(0f, 0.76f, 0f);
        private static readonly Vector3 FlameSize = new Vector3(0.13f, 0.17f, 0.13f);

        private readonly Mesh stick;
        private readonly Mesh head;
        private readonly Mesh flame;
        private readonly RenderParams woodParams;
        private readonly RenderParams headParams;
        private readonly RenderParams flameParams;
        private readonly List<Matrix4x4> bodies = new List<Matrix4x4>();
        private readonly List<Matrix4x4> flames = new List<Matrix4x4>();
        private Matrix4x4[] batch = new Matrix4x4[64];

        public TorchViews(Material wood, Material headMaterial, Material flameMaterial)
        {
            stick = BoxMesh.Build("Torch stick", new[] { new[] { (new Vector3(0f, 0.3f, 0f), new Vector3(0.1f, 0.6f, 0.1f)) } });
            head = BoxMesh.Build("Torch head", new[] { new[] { (new Vector3(0f, 0.63f, 0f), new Vector3(0.14f, 0.11f, 0.14f)) } });
            flame = BoxMesh.Build("Torch flame", new[] { new[] { (Vector3.zero, Vector3.one) } });
            woodParams = new RenderParams(wood) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            headParams = new RenderParams(headMaterial) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            flameParams = new RenderParams(flameMaterial) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        }

        public void Render(World world, BlockLight light, Vector3 viewer)
        {
            if (light == null || light.Count == 0) return;

            bodies.Clear();
            flames.Clear();
            float time = Time.time;
            var bounds = new Bounds(viewer, Vector3.zero);
            foreach (BlockPos pos in light.Sources)
            {
                ushort block = world.GetBlock(pos);
                if (!Blocks.IsTorch(block)) continue;

                var cell = new Vector3(pos.X + 0.5f, pos.Y, pos.Z + 0.5f);
                if ((cell - viewer).sqrMagnitude > ViewDistance * ViewDistance) continue;

                Matrix4x4 body = Placement(pos, block, cell);
                bodies.Add(body);

                // Two waves per torch, out of step from one torch to the next.
                float phase = (pos.X * 7 + pos.Y * 13 + pos.Z * 31) * 0.37f;
                float flicker = 1f + 0.12f * Mathf.Sin(time * 9f + phase) + 0.06f * Mathf.Sin(time * 23f + phase * 1.7f);
                flames.Add(body * Matrix4x4.TRS(FlameCentre, Quaternion.identity, Vector3.Scale(FlameSize, new Vector3(flicker, flicker * flicker, flicker))));
                bounds.Encapsulate(cell);
            }

            if (bodies.Count == 0) return;

            bounds.Expand(2f);
            Draw(woodParams, stick, bodies, bounds);
            Draw(headParams, head, bodies, bounds);
            Draw(flameParams, flame, flames, bounds);
        }

        // Where a torch's stick stands: upright in the middle of its cell, or from near its support,
        // leaning away from it.
        private static Matrix4x4 Placement(BlockPos pos, ushort block, Vector3 cell)
        {
            BlockPos support = Torches.SupportOf(pos, block);
            var toward = new Vector3(support.X - pos.X, 0f, support.Z - pos.Z);
            if (toward == Vector3.zero) return Matrix4x4.Translate(cell);

            Vector3 foot = cell + toward * WallInset + Vector3.up * WallFoot;
            Quaternion lean = Quaternion.FromToRotation(Vector3.up, (Vector3.up - toward * WallLean).normalized);
            return Matrix4x4.TRS(foot, lean, Vector3.one);
        }

        private void Draw(RenderParams parameters, Mesh mesh, List<Matrix4x4> placed, Bounds bounds)
        {
            if (batch.Length < placed.Count) batch = new Matrix4x4[Mathf.NextPowerOfTwo(placed.Count)];
            placed.CopyTo(batch);
            parameters.worldBounds = bounds;
            for (int start = 0; start < placed.Count; start += MaxInstancesPerCall)
            {
                Graphics.RenderMeshInstanced(parameters, mesh, 0, batch, Mathf.Min(MaxInstancesPerCall, placed.Count - start), start);
            }
        }
    }
}
