using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Where the hammer would build, or what it would take back: the edges of each block's boxes (its
    // shape's, Shapes.GetBoxes), or for a slope and the roof's corners the edges of the shape as drawn,
    // as thin bars, green where it can go, red where it cannot or where it is taken back. Drawn this
    // frame only, instanced, unlit, as the storage preview.
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

            // A slope and the roof's corners are outlined as they are drawn, not by the quarter steps
            // they collide by, which looked like stairs.
            Vector3[] edges = SlopedEdges(Shapes.ShapeOf(block));
            if (Blocks.IsConstruction(block) && edges != null)
            {
                var origin = new Vector3(cell.X, cell.Y, cell.Z);
                int facing = Shapes.FacingOf(block);
                for (int i = 0; i < edges.Length; i += 2)
                {
                    AddEdge(origin + Outward(ChunkMeshBuilder.TurnPoint(edges[i], facing)), origin + Outward(ChunkMeshBuilder.TurnPoint(edges[i + 1], facing)), valid);
                }

                return;
            }

            boxes.Clear();
            Shapes.GetBoxes(world, cell, block, boxes);
            foreach ((System.Numerics.Vector3 min, System.Numerics.Vector3 max) in boxes)
            {
                AddBox(min.ToUnity(), max.ToUnity(), valid);
            }
        }

        // The edges of a sloped shape given facing +z, in pairs, as ChunkMeshBuilder draws it (AddSlope,
        // AddOuterCorner, AddInnerCorner); null for a shape made of boxes.
        private static Vector3[] SlopedEdges(Shape shape)
        {
            switch (shape)
            {
                case Shape.Slope: return SlopeEdges;
                case Shape.OuterCorner: return OuterCornerEdges;
                case Shape.InnerCorner: return InnerCornerEdges;
                default: return null;
            }
        }

        private static readonly Vector3 P000 = new Vector3(0, 0, 0), P100 = new Vector3(1, 0, 0), P101 = new Vector3(1, 0, 1), P001 = new Vector3(0, 0, 1);
        private static readonly Vector3 P110 = new Vector3(1, 1, 0), P111 = new Vector3(1, 1, 1), P011 = new Vector3(0, 1, 1);

        // Rising toward +z: the bottom, the back's two uprights and its top, the ramp's two sides.
        private static readonly Vector3[] SlopeEdges =
        {
            P000, P100, P100, P101, P101, P001, P001, P000,
            P101, P111, P001, P011, P011, P111,
            P000, P011, P100, P111
        };

        // A quarter of a pyramid, its tip at (1, 1, 1): the bottom, the hip from (0, 0, 0) to the tip,
        // and the edges from the three other corners up to it.
        private static readonly Vector3[] OuterCornerEdges =
        {
            P000, P100, P100, P101, P101, P001, P001, P000,
            P000, P111, P100, P111, P101, P111, P001, P111
        };

        // A valley, low only at (0, 0, 0): the bottom, three uprights, the two high top edges, the two
        // sloping edges from the low corner and the valley's crease.
        private static readonly Vector3[] InnerCornerEdges =
        {
            P000, P100, P100, P101, P101, P001, P001, P000,
            P100, P110, P101, P111, P001, P011,
            P110, P111, P111, P011,
            P000, P110, P000, P011, P000, P111
        };

        // A point of the cell pushed a little away from its middle, so that the edges show over its faces.
        private static Vector3 Outward(Vector3 point)
        {
            var middle = new Vector3(0.5f, 0.5f, 0.5f);
            return middle + (point - middle) * (1f + 2f * Inflate);
        }

        // A bar from a to b.
        private void AddEdge(Vector3 a, Vector3 b, bool valid)
        {
            Vector3 along = b - a;
            float length = along.magnitude;
            if (length < 1e-4f) return;

            (valid ? greenBars : redBars).Add(Matrix4x4.TRS((a + b) * 0.5f, Quaternion.FromToRotation(Vector3.right, along / length), new Vector3(length + Thickness, Thickness, Thickness)));
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
