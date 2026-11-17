using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A building shape (Shapes) drawn as the player sees it, from behind and above: the cell's far side
    // (+z, the way the player looks) up on the glyph, its right side (+x) to the right, so that a shape's
    // rotation reads the way it will be placed. Its top, front and right faces are filled in three
    // shades and outlined. Every shape is drawn at the same scale, so a post looks as thin beside a
    // full block as it is. Colours come from the style sheet: --shape-top, --shape-left (the right
    // side), --shape-right (the front) and --shape-line.
    public class ShapeGlyph : VisualElement
    {
        private static readonly CustomStyleProperty<Color> TopProperty = new CustomStyleProperty<Color>("--shape-top");
        private static readonly CustomStyleProperty<Color> LeftProperty = new CustomStyleProperty<Color>("--shape-left");
        private static readonly CustomStyleProperty<Color> RightProperty = new CustomStyleProperty<Color>("--shape-right");
        private static readonly CustomStyleProperty<Color> LineProperty = new CustomStyleProperty<Color>("--shape-line");

        // An oblique view: going away (+z) moves a point right by Aside and up by Rise; up (+y) moves it up.
        private const float Aside = 0.35f;
        private const float Rise = 0.5f;
        private static readonly Vector3 TowardViewer = new Vector3(Aside, Rise, -1f);

        private readonly List<(Vector3[] points, Vector3 normal)> faces;
        private Color top = new Color(0.86f, 0.70f, 0.47f);
        private Color side = new Color(0.62f, 0.47f, 0.29f);
        private Color front = new Color(0.74f, 0.57f, 0.36f);
        private Color line = new Color(0.17f, 0.12f, 0.08f);

        // A shape facing a way (an axis for beams, fences and low walls), as Shapes.Make takes it.
        public ShapeGlyph(Shape shape, int facing)
        {
            faces = FacesOf(shape, facing);
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(TopProperty, out Color resolvedTop)) top = resolvedTop;
            if (e.customStyle.TryGetValue(LeftProperty, out Color resolvedSide)) side = resolvedSide;
            if (e.customStyle.TryGetValue(RightProperty, out Color resolvedFront)) front = resolvedFront;
            if (e.customStyle.TryGetValue(LineProperty, out Color resolvedLine)) line = resolvedLine;
            MarkDirtyRepaint();
        }

        // The faces that show, each with its outward normal, farthest first.
        private static List<(Vector3[] points, Vector3 normal)> FacesOf(Shape shape, int facing)
        {
            var all = new List<(Vector3[], Vector3)>();
            switch (shape)
            {
                case Shape.Slope:
                    // As ChunkMeshBuilder.AddSlope: rising toward +z, then turned.
                    Polygon(all, facing, Vector3.down, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1));
                    Polygon(all, facing, Vector3.forward, new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1));
                    Polygon(all, facing, Vector3.right, new Vector3(1, 0, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1));
                    Polygon(all, facing, Vector3.left, new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 0));
                    Polygon(all, facing, Vector3.up, new Vector3(0, 0, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 0, 0));
                    break;
                case Shape.OuterCorner:
                    // As AddOuterCorner: its tip at (1, 1, 1).
                    Polygon(all, facing, Vector3.up, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 1));
                    Polygon(all, facing, Vector3.up, new Vector3(0, 0, 0), new Vector3(1, 1, 1), new Vector3(0, 0, 1));
                    Polygon(all, facing, Vector3.right, new Vector3(1, 0, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1));
                    Polygon(all, facing, Vector3.forward, new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1));
                    Polygon(all, facing, Vector3.down, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1));
                    break;
                case Shape.InnerCorner:
                    // As AddInnerCorner: high along x 1 and z 1, low at (0, 0, 0).
                    Polygon(all, facing, Vector3.up, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1));
                    Polygon(all, facing, Vector3.up, new Vector3(0, 0, 0), new Vector3(1, 1, 1), new Vector3(0, 1, 1));
                    Polygon(all, facing, Vector3.right, new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1));
                    Polygon(all, facing, Vector3.forward, new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1));
                    Polygon(all, facing, Vector3.left, new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1));
                    Polygon(all, facing, Vector3.back, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0));
                    Polygon(all, facing, Vector3.down, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1));
                    break;
                case Shape.Fence:
                    // As ChunkMeshBuilder.AddFence: a post and two rails toward each side of its axis.
                    Box(all, new Vector3(0.39f, 0f, 0.39f), new Vector3(0.61f, 1f, 0.61f));
                    int sides = Shapes.AxisMask(facing);
                    foreach ((float bottom, float top) in new[] { (0.3f, 0.45f), (0.7f, 0.85f) })
                    {
                        if ((sides & Shapes.TowardPlusX) != 0) Box(all, new Vector3(0.61f, bottom, 0.45f), new Vector3(1f, top, 0.55f));
                        if ((sides & Shapes.TowardMinusX) != 0) Box(all, new Vector3(0f, bottom, 0.45f), new Vector3(0.39f, top, 0.55f));
                        if ((sides & Shapes.TowardPlusZ) != 0) Box(all, new Vector3(0.45f, bottom, 0.61f), new Vector3(0.55f, top, 1f));
                        if ((sides & Shapes.TowardMinusZ) != 0) Box(all, new Vector3(0.45f, bottom, 0f), new Vector3(0.55f, top, 0.39f));
                    }

                    break;
                case Shape.Ladder:
                    // As AddLadder: two rails and four rungs against the edge of its facing.
                    float back = 1f - Shapes.LadderThickness;
                    TurnedBox(all, facing, new Vector3(0.1f, 0f, back), new Vector3(0.22f, 1f, 1f));
                    TurnedBox(all, facing, new Vector3(0.78f, 0f, back), new Vector3(0.9f, 1f, 1f));
                    for (int rung = 0; rung < 4; rung++)
                    {
                        float y = 0.13f + rung * 0.25f;
                        TurnedBox(all, facing, new Vector3(0.22f, y, back + 0.025f), new Vector3(0.78f, y + 0.07f, 0.97f));
                    }

                    break;
                default:
                    var boxes = new List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>();
                    int connections = Shapes.HasAxis(shape) ? Shapes.AxisMask(facing) : 0;
                    Shapes.GetLocalBoxes(shape, facing, connections, boxes);
                    foreach ((System.Numerics.Vector3 min, System.Numerics.Vector3 max) in boxes) Box(all, min.ToUnity(), max.ToUnity());
                    break;
            }

            var shown = new List<(Vector3[] points, Vector3 normal)>();
            foreach ((Vector3[] points, Vector3 normal) in all)
            {
                if (Vector3.Dot(normal, TowardViewer) > 1e-3f) shown.Add((points, normal));
            }

            shown.Sort((a, b) => Depth(a.points).CompareTo(Depth(b.points)));
            return shown;
        }

        // How near the viewer a face's middle is.
        private static float Depth(Vector3[] points)
        {
            Vector3 sum = Vector3.zero;
            foreach (Vector3 point in points) sum += point;
            return Vector3.Dot(sum / points.Length, TowardViewer);
        }

        // A face given facing +z, turned; its normal from its corners, made to agree with outward
        // (also turned), and its corners ordered to match.
        private static void Polygon(List<(Vector3[], Vector3)> into, int facing, Vector3 outward, params Vector3[] given)
        {
            var points = new Vector3[given.Length];
            for (int i = 0; i < given.Length; i++) points[i] = ChunkMeshBuilder.TurnPoint(given[i], facing);
            Vector3 hint = ChunkMeshBuilder.TurnPoint(outward + new Vector3(0.5f, 0.5f, 0.5f), facing) - new Vector3(0.5f, 0.5f, 0.5f);
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 a = points[i], b = points[(i + 1) % points.Length];
                normal += new Vector3((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y));
            }

            normal = normal.normalized;
            if (Vector3.Dot(normal, hint) < 0f) normal = -normal;
            into.Add((points, normal));
        }

        // A box given facing +z, turned as Shapes.Turned turns it.
        private static void TurnedBox(List<(Vector3[], Vector3)> into, int facing, Vector3 min, Vector3 max)
        {
            (System.Numerics.Vector3 a, System.Numerics.Vector3 b) = Shapes.Turned(min.ToNumerics(), max.ToNumerics(), facing);
            Box(into, a.ToUnity(), b.ToUnity());
        }

        // The six faces of a box.
        private static void Box(List<(Vector3[], Vector3)> into, Vector3 min, Vector3 max)
        {
            into.Add((new[] { new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z) }, Vector3.up));
            into.Add((new[] { new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(min.x, max.y, min.z) }, Vector3.back));
            into.Add((new[] { new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(max.x, max.y, min.z) }, Vector3.right));
            into.Add((new[] { new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z) }, Vector3.forward));
            into.Add((new[] { new Vector3(min.x, min.y, min.z), new Vector3(min.x, min.y, max.z), new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z) }, Vector3.left));
            into.Add((new[] { new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z) }, Vector3.down));
        }

        private void Draw(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;

            // A full cell spans 1 + Aside across and 1 + Rise high: every shape is fitted in that frame.
            float scale = Mathf.Min(rect.width / (1f + Aside), rect.height / (1f + Rise)) * 0.86f;
            Vector2 origin = rect.center + scale * new Vector2(-(1f + Aside) * 0.5f, (1f + Rise) * 0.5f);
            Vector2 Project(Vector3 p) => origin + scale * new Vector2(p.x + p.z * Aside, -p.y - p.z * Rise);

            Painter2D painter = context.painter2D;
            painter.lineJoin = LineJoin.Round;

            // The whole cell's edges, faint, so that a slab or a beam shows where it lies in it.
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(top.r, top.g, top.b, 0.3f);
            for (int a = 0; a <= 1; a++)
            {
                for (int b = 0; b <= 1; b++)
                {
                    Edge(painter, Project(new Vector3(0f, a, b)), Project(new Vector3(1f, a, b)));
                    Edge(painter, Project(new Vector3(a, 0f, b)), Project(new Vector3(a, 1f, b)));
                    Edge(painter, Project(new Vector3(a, b, 0f)), Project(new Vector3(a, b, 1f)));
                }
            }

            painter.lineWidth = 1.5f;
            painter.strokeColor = line;
            foreach ((Vector3[] points, Vector3 normal) in faces)
            {
                // Shaded by how much it faces up, toward the viewer or to the right.
                float up = Mathf.Max(normal.y, 0f), toward = Mathf.Max(-normal.z, 0f), right = Mathf.Max(normal.x, 0f);
                float sum = up + toward + right;
                Color colour = sum <= 0f ? front : (top * up + front * toward + side * right) / sum;
                colour.a = 1f;

                painter.fillColor = colour;
                painter.BeginPath();
                painter.MoveTo(Project(points[0]));
                for (int i = 1; i < points.Length; i++) painter.LineTo(Project(points[i]));
                painter.ClosePath();
                painter.Fill();
                painter.Stroke();
            }
        }

        private static void Edge(Painter2D painter, Vector2 from, Vector2 to)
        {
            painter.BeginPath();
            painter.MoveTo(from);
            painter.LineTo(to);
            painter.Stroke();
        }
    }
}
