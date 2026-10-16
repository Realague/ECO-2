using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The local player's arrow on the maps, pointing up until it is rotated. Its colours come
    // from the style sheet: --arrow-fill and --arrow-outline.
    [UxmlElement]
    public partial class MapArrow : VisualElement
    {
        private static readonly CustomStyleProperty<Color> FillProperty = new CustomStyleProperty<Color>("--arrow-fill");
        private static readonly CustomStyleProperty<Color> OutlineProperty = new CustomStyleProperty<Color>("--arrow-outline");

        private Color fill = Color.white;
        private Color outline = Color.black;

        public MapArrow()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent e)
        {
            if (e.customStyle.TryGetValue(FillProperty, out Color resolvedFill)) fill = resolvedFill;
            if (e.customStyle.TryGetValue(OutlineProperty, out Color resolvedOutline)) outline = resolvedOutline;
            MarkDirtyRepaint();
        }

        // An arrowhead with a notch at the back, inset by the outline's width so that it is not cut.
        private void Draw(MeshGenerationContext context)
        {
            const float inset = 1.5f;
            float width = contentRect.width;
            float height = contentRect.height;

            Painter2D painter = context.painter2D;
            painter.lineWidth = 2f;
            painter.lineJoin = LineJoin.Round;
            painter.fillColor = fill;
            painter.strokeColor = outline;

            painter.BeginPath();
            painter.MoveTo(new Vector2(width * 0.5f, inset));
            painter.LineTo(new Vector2(width - inset, height - inset));
            painter.LineTo(new Vector2(width * 0.5f, height * 0.72f));
            painter.LineTo(new Vector2(inset, height - inset));
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
        }
    }
}
