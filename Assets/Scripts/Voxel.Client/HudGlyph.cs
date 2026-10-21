using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // A small drawn sign for the HUD: the sun, the moon or a place marker, in the colour of the
    // style sheet's --glyph-colour (Kind sets which, from code or UXML).
    [UxmlElement]
    public partial class HudGlyph : VisualElement
    {
        public enum Shape
        {
            Sun,
            Moon,
            Pin
        }

        private static readonly CustomStyleProperty<Color> ColourProperty = new CustomStyleProperty<Color>("--glyph-colour");

        private Shape kind;
        private Color colour = Color.white;

        public HudGlyph()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        [UxmlAttribute]
        public Shape Kind
        {
            get => kind;
            set
            {
                if (kind == value) return;

                kind = value;
                MarkDirtyRepaint();
            }
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(ColourProperty, out Color resolved)) colour = resolved;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            float size = Mathf.Min(contentRect.width, contentRect.height);
            var centre = new Vector2(contentRect.width * 0.5f, contentRect.height * 0.5f);
            Painter2D painter = context.painter2D;
            painter.fillColor = colour;
            painter.strokeColor = colour;
            painter.lineCap = LineCap.Round;

            switch (kind)
            {
                case Shape.Sun:
                    painter.BeginPath();
                    painter.Arc(centre, size * 0.22f, 0f, 360f);
                    painter.Fill();
                    painter.lineWidth = Mathf.Max(1.5f, size * 0.08f);
                    for (int ray = 0; ray < 8; ray++)
                    {
                        float angle = ray * Mathf.PI / 4f;
                        var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                        painter.BeginPath();
                        painter.MoveTo(centre + direction * size * 0.33f);
                        painter.LineTo(centre + direction * size * 0.46f);
                        painter.Stroke();
                    }

                    break;
                case Shape.Moon:
                    // A crescent: the outer arc of one disc, back along the inner arc of a second.
                    float radius = size * 0.4f;
                    var inner = centre + new Vector2(radius * 0.45f, -radius * 0.2f);
                    painter.BeginPath();
                    painter.Arc(centre, radius, 35f, 330f);
                    painter.Arc(inner, radius * 0.82f, 280f, 85f, ArcDirection.CounterClockwise);
                    painter.ClosePath();
                    painter.Fill();
                    break;
                default:
                    // A marker: a disc tapering to a point, with a hole.
                    float head = size * 0.3f;
                    var top = new Vector2(centre.x, size * 0.38f);
                    painter.BeginPath();
                    painter.Arc(top, head, 150f, 390f);
                    painter.LineTo(new Vector2(centre.x, size * 0.95f));
                    painter.ClosePath();
                    painter.Fill();
                    painter.fillColor = new Color(0f, 0f, 0f, 0.35f);
                    painter.BeginPath();
                    painter.Arc(top, head * 0.4f, 0f, 360f);
                    painter.Fill();
                    break;
            }
        }
    }
}
