using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The stomach drawn as a pie: a dark disc the size of a full stomach, and over it four slices,
    // one per nutrient (carbs, protein, fat, vitamins, from the top and clockwise), as wide as its
    // share and as long as the stomach is full: the slices' radius goes with the square root of
    // fullness, so their area is the calories. Colours come from the style sheet (--carbs-colour,
    // --protein-colour, --fat-colour, --vitamins-colour, --pie-back, --pie-rim).
    [UxmlElement]
    public partial class StomachPie : VisualElement
    {
        private static readonly CustomStyleProperty<Color>[] SliceProperties =
        {
            new CustomStyleProperty<Color>("--carbs-colour"),
            new CustomStyleProperty<Color>("--protein-colour"),
            new CustomStyleProperty<Color>("--fat-colour"),
            new CustomStyleProperty<Color>("--vitamins-colour")
        };

        private static readonly CustomStyleProperty<Color> BackProperty = new CustomStyleProperty<Color>("--pie-back");
        private static readonly CustomStyleProperty<Color> RimProperty = new CustomStyleProperty<Color>("--pie-rim");

        private readonly Color[] sliceColours = { Color.red, new Color(1f, 0.6f, 0.2f), Color.yellow, Color.green };
        private readonly float[] shares = new float[Nutrition.NutrientCount];
        private Color back = new Color(0f, 0f, 0f, 0.4f);
        private Color rim = Color.white;
        private float fullness;

        public StomachPie()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyleResolved);
        }

        public void Show(Stomach stomach)
        {
            fullness = Mathf.Clamp01(stomach.Fullness);
            for (int i = 0; i < shares.Length; i++)
            {
                shares[i] = stomach.Share(i);
            }

            MarkDirtyRepaint();
        }

        private void OnCustomStyleResolved(CustomStyleResolvedEvent e)
        {
            for (int i = 0; i < SliceProperties.Length; i++)
            {
                if (e.customStyle.TryGetValue(SliceProperties[i], out Color colour)) sliceColours[i] = colour;
            }

            if (e.customStyle.TryGetValue(BackProperty, out Color resolvedBack)) back = resolvedBack;
            if (e.customStyle.TryGetValue(RimProperty, out Color resolvedRim)) rim = resolvedRim;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            float size = Mathf.Min(contentRect.width, contentRect.height);
            if (size <= 0f) return;

            var centre = new Vector2(contentRect.width * 0.5f, contentRect.height * 0.5f);
            float rimWidth = Mathf.Max(1.5f, size * 0.03f);
            float full = size * 0.5f - rimWidth;
            Painter2D painter = context.painter2D;

            painter.fillColor = back;
            painter.BeginPath();
            painter.Arc(centre, full, 0f, 360f);
            painter.Fill();

            float radius = full * Mathf.Sqrt(fullness);
            if (radius > 0.5f)
            {
                // Angles in degrees from the right, clockwise on screen: the pie starts at the top.
                float angle = -90f;
                for (int i = 0; i < shares.Length; i++)
                {
                    if (shares[i] <= 0f) continue;

                    float end = angle + shares[i] * 360f;
                    painter.fillColor = sliceColours[i];
                    painter.BeginPath();
                    painter.MoveTo(centre);
                    painter.Arc(centre, radius, angle, end);
                    painter.ClosePath();
                    painter.Fill();
                    angle = end;
                }
            }

            painter.strokeColor = rim;
            painter.lineWidth = rimWidth;
            painter.BeginPath();
            painter.Arc(centre, full + rimWidth * 0.5f, 0f, 360f);
            painter.Stroke();
        }
    }
}
