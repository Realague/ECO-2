using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Where the hammer would build, or what it would take back: the edges of each block's boxes (its
    // shape's, Shapes.GetBoxes) as thin bars, green where it can go, red where it cannot or where it is
    // taken back. Drawn this frame only, instanced, unlit, as the storage preview.
    internal sealed class BuildPreview
    {
        private const float Thickness = 0.025f;
        private const float Inflate = 0.004f;

        private readonly Mesh bar;
        private readonly RenderParams green;
        private readonly RenderParams red;
        private readonly List<Matrix4x4> greenBars = new List<Matrix4x4>();
        private readonly List<Matrix4x4> redBars = new List<Matrix4x4>();
        private readonly List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)> boxes = new List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>();

        public BuildPreview()
        {
            bar = BoxMesh.Build("BuildPreviewBar", new[] { new List<(Vector3, Vector3)> { (Vector3.zero, Vector3.one) } });
            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            green = Params(unlit, new Color(0.45f, 0.95f, 0.45f));
            red = Params(unlit, new Color(0.95f, 0.35f, 0.3f));
        }

        // A block as it would stand at cell (its shape's boxes, a door's two cells).
        public void AddBlock(World world, BlockPos cell, ushort block, bool valid)
        {
            if (Blocks.IsDoor(block))
            {
                AddBox(new Vector3(cell.X, cell.Y, cell.Z), new Vector3(cell.X + 1, cell.Y + 2, cell.Z + 1), valid);
                return;
            }

            boxes.Clear();
            Shapes.GetBoxes(world, cell, block, boxes);
            foreach ((System.Numerics.Vector3 min, System.Numerics.Vector3 max) in boxes)
            {
                AddBox(min.ToUnity(), max.ToUnity(), valid);
            }
        }

        // The twelve edges of a box, a little outside it so that they show over its faces.
        public void AddBox(Vector3 min, Vector3 max, bool valid)
        {
            List<Matrix4x4> into = valid ? greenBars : redBars;
            min -= Vector3.one * Inflate;
            max += Vector3.one * Inflate;
            Vector3 size = max - min;
            Vector3 middle = (min + max) * 0.5f;
            for (int a = 0; a <= 1; a++)
            {
                for (int b = 0; b <= 1; b++)
                {
                    float xa = a == 0 ? min.x : max.x;
                    float ya = a == 0 ? min.y : max.y;
                    float yb = b == 0 ? min.y : max.y;
                    float zb = b == 0 ? min.z : max.z;
                    into.Add(Matrix4x4.TRS(new Vector3(middle.x, ya, zb), Quaternion.identity, new Vector3(size.x + Thickness, Thickness, Thickness)));
                    into.Add(Matrix4x4.TRS(new Vector3(xa, middle.y, zb), Quaternion.identity, new Vector3(Thickness, size.y + Thickness, Thickness)));
                    into.Add(Matrix4x4.TRS(new Vector3(xa, yb, middle.z), Quaternion.identity, new Vector3(Thickness, Thickness, size.z + Thickness)));
                }
            }
        }

        // Draws what was added this frame, then forgets it.
        public void Render()
        {
            Draw(green, greenBars);
            Draw(red, redBars);
        }

        private void Draw(RenderParams look, List<Matrix4x4> bars)
        {
            if (bars.Count > 0 && look.material != null) Graphics.RenderMeshInstanced(look, bar, 0, bars);
            bars.Clear();
        }

        private static RenderParams Params(Shader shader, Color colour)
        {
            if (shader == null) return default;

            var material = new Material(shader) { name = "Build preview", enableInstancing = true };
            material.SetColor("_BaseColor", colour);
            return new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        }
    }
}
