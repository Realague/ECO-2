using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A building shape (Shapes) drawn in isometric view: the cell seen from above, from the +x +z side,
    // its top, its +x side and its +z side filled in three shades and outlined. Every shape is drawn at
    // the same scale, so a post looks as thin beside a full block as it is. Colours come from the style
    // sheet: --shape-top, --shape-left, --shape-right and --shape-line.
    public class ShapeGlyph : VisualElement
    {
        private static readonly CustomStyleProperty<Color> TopProperty = new CustomStyleProperty<Color>("--shape-top");
        private static readonly CustomStyleProperty<Color> LeftProperty = new CustomStyleProperty<Color>("--shape-left");
        private static readonly CustomStyleProperty<Color> RightProperty = new CustomStyleProperty<Color>("--shape-right");
        private static readonly CustomStyleProperty<Color> LineProperty = new CustomStyleProperty<Color>("--shape-line");

        // Isometric axes: x goes right and down, z left and down, y up.
        private const float Across = 0.8660254f;
        private const float Down = 0.5f;

        private readonly Shape shape;
        private Color top = new Color(0.86f, 0.70f, 0.47f);
        private Color left = new Color(0.62f, 0.47f, 0.29f);
        private Color right = new Color(0.74f, 0.57f, 0.36f);
        private Color line = new Color(0.17f, 0.12f, 0.08f);

        public ShapeGlyph(Shape shape)
        {
            this.shape = shape;
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(TopProperty, out Color resolvedTop)) top = resolvedTop;
            if (e.customStyle.TryGetValue(LeftProperty, out Color resolvedLeft)) left = resolvedLeft;
            if (e.customStyle.TryGetValue(RightProperty, out Color resolvedRight)) right = resolvedRight;
            if (e.customStyle.TryGetValue(LineProperty, out Color resolvedLine)) line = resolvedLine;
            MarkDirtyRepaint();
        }

        // The boxes of a shape in its cell (0 to 1), back ones first; walls and slopes face -z, so that
        // the wall stands at the back and the slope's ramp turns toward the viewer.
        private static List<(Vector3 min, Vector3 max)> BoxesOf(Shape shape)
        {
            const float post = Shapes.PostWidth * 0.5f;
            var boxes = new List<(Vector3, Vector3)>();
            switch (shape)
            {
                case Shape.Wall:
                    boxes.Add((Vector3.zero, new Vector3(1f, 1f, Shapes.WallThickness)));
                    break;
                case Shape.Post:
                    boxes.Add((new Vector3(0.5f - post, 0f, 0.5f - post), new Vector3(0.5f + post, 1f, 0.5f + post)));
                    break;
                case Shape.Floor:
                    boxes.Add((Vector3.zero, new Vector3(1f, 0.5f, 1f)));
                    break;
                case Shape.FlatRoof:
                    boxes.Add((new Vector3(0f, 0.5f, 0f), Vector3.one));
                    break;
                case Shape.Fence:
                    // Rails along x, the part behind the post first, then the post, then the part before it.
                    foreach ((float from, float to) in new[] { (0f, 0.39f), (0.61f, 1f) })
                    {
                        boxes.Add((new Vector3(from, 0.3f, 0.45f), new Vector3(to, 0.45f, 0.55f)));
                        boxes.Add((new Vector3(from, 0.7f, 0.45f), new Vector3(to, 0.85f, 0.55f)));
                        if (from == 0f) boxes.Add((new Vector3(0.39f, 0f, 0.39f), new Vector3(0.61f, 1f, 0.61f)));
                    }

                    break;
                case Shape.Stairs:
                    // Rising toward -z: the high step behind, the low one before it.
                    boxes.Add((Vector3.zero, new Vector3(1f, 1f, 0.5f)));
                    boxes.Add((new Vector3(0f, 0f, 0.5f), new Vector3(1f, 0.5f, 1f)));
                    break;
                case Shape.Ladder:
                    // Against the back edge (-z): a rail, the rungs, then the rail in front of them.
                    boxes.Add((new Vector3(0.1f, 0f, 0f), new Vector3(0.22f, 1f, Shapes.LadderThickness)));
                    for (int rung = 0; rung < 4; rung++)
                    {
                        float y = 0.13f + rung * 0.25f;
                        boxes.Add((new Vector3(0.22f, y, 0.03f), new Vector3(0.78f, y + 0.07f, 0.1f)));
                    }

                    boxes.Add((new Vector3(0.78f, 0f, 0f), new Vector3(0.9f, 1f, Shapes.LadderThickness)));
                    break;
                case Shape.Slope:
                case Shape.OuterCorner:
                case Shape.InnerCorner:
                    break;
                default:
                    boxes.Add((Vector3.zero, Vector3.one));
                    break;
            }

            return boxes;
        }

        private void Draw(MeshGenerationContext context)
        {
            Rect rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f) return;

            // A full cell spans 2 × Across wide and 2 high: every shape is fitted in that frame.
            float scale = Mathf.Min(rect.width / (2f * Across), rect.height / 2f) * 0.86f;
            Vector2 centre = rect.center;
            Vector2 Project(float x, float y, float z) => centre + scale * new Vector2((x - z) * Across, (x + z) * Down - y);

            Painter2D painter = context.painter2D;
            painter.lineJoin = LineJoin.Round;

            // The whole cell's edges, faint, so that a slab shows whether it is low or high in it.
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(top.r, top.g, top.b, 0.3f);
            for (int a = 0; a <= 1; a++)
            {
                for (int b = 0; b <= 1; b++)
                {
                    Edge(painter, Project(0f, a, b), Project(1f, a, b));
                    Edge(painter, Project(a, 0f, b), Project(a, 1f, b));
                    Edge(painter, Project(a, b, 0f), Project(a, b, 1f));
                }
            }

            painter.lineWidth = 1.5f;
            painter.strokeColor = line;

            if (shape == Shape.Slope)
            {
                // Rising toward -z: the ramp from the low front edge (z = 1) to the high back one, and the +x side.
                Face(painter, top, Project(0f, 1f, 0f), Project(1f, 1f, 0f), Project(1f, 0f, 1f), Project(0f, 0f, 1f));
                Face(painter, right, Project(1f, 0f, 0f), Project(1f, 1f, 0f), Project(1f, 0f, 1f));
                return;
            }

            // Corners turned to rise toward the back corner (x 0, z 0): the slopes face the viewer.
            Color rightSlope = Color.Lerp(top, right, 0.45f);
            Color leftSlope = Color.Lerp(top, left, 0.45f);
            if (shape == Shape.OuterCorner)
            {
                // Its tip at the back corner, low along the two front edges.
                Face(painter, rightSlope, Project(0f, 1f, 0f), Project(1f, 0f, 0f), Project(1f, 0f, 1f));
                Face(painter, leftSlope, Project(0f, 1f, 0f), Project(1f, 0f, 1f), Project(0f, 0f, 1f));
                return;
            }

            if (shape == Shape.InnerCorner)
            {
                // High along the two back edges, low at the front corner: the two sides, then the slopes.
                Face(painter, right, Project(1f, 0f, 0f), Project(1f, 1f, 0f), Project(1f, 0f, 1f));
                Face(painter, left, Project(0f, 0f, 1f), Project(0f, 1f, 1f), Project(1f, 0f, 1f));
                Face(painter, rightSlope, Project(0f, 1f, 0f), Project(0f, 1f, 1f), Project(1f, 0f, 1f));
                Face(painter, leftSlope, Project(0f, 1f, 0f), Project(1f, 1f, 0f), Project(1f, 0f, 1f));
                return;
            }

            foreach ((Vector3 min, Vector3 max) in BoxesOf(shape))
            {
                Face(painter, left, Project(min.x, min.y, max.z), Project(max.x, min.y, max.z), Project(max.x, max.y, max.z), Project(min.x, max.y, max.z));
                Face(painter, right, Project(max.x, min.y, min.z), Project(max.x, min.y, max.z), Project(max.x, max.y, max.z), Project(max.x, max.y, min.z));
                Face(painter, top, Project(min.x, max.y, min.z), Project(max.x, max.y, min.z), Project(max.x, max.y, max.z), Project(min.x, max.y, max.z));
            }
        }

        private static void Edge(Painter2D painter, Vector2 from, Vector2 to)
        {
            painter.BeginPath();
            painter.MoveTo(from);
            painter.LineTo(to);
            painter.Stroke();
        }

        private static void Face(Painter2D painter, Color colour, params Vector2[] corners)
        {
            painter.fillColor = colour;
            painter.BeginPath();
            painter.MoveTo(corners[0]);
            for (int i = 1; i < corners.Length; i++)
            {
                painter.LineTo(corners[i]);
            }

            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
        }
    }
}
