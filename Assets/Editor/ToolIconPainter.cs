using System;
using System.Collections.Generic;
using UnityEngine;
using Voxel.Core;

// Paints the icons of the tools and of the arrow for ItemIconBuilder, drawn like illustrations
// rather than rendered from their boxes: laid on the diagonal (handle at the lower left, working end
// at the upper right), each part shaded as if lit from the upper left (wood with its grain, leather
// with its wrapping, steel with bright bevels), outlined in dark brown, each part over the ones
// behind it too, above a soft shadow, so they read on the light item cards and the dark hotbar alike.
// A tool is designed in a frame of its own (the handle along +y, the working end up, its front toward
// -x), then turned and fitted to the icon. Supersampled four times for smooth edges.
internal static class ToolIconPainter
{
    private const int Size = ItemIconBuilder.IconSize;
    private const int Supersample = 4;
    private const float Margin = 7f;
    private const float OuterOutline = 2f;
    private const float InnerOutline = 1.1f;
    private const float Bevel = 2.6f;
    private static readonly Vector2 ShadowOffset = new Vector2(2f, -3f);
    private const float ShadowAlpha = 0.35f;

    // Light from the upper left and the front, in icon space (x right, y up, z toward the viewer).
    private static readonly Vector3 LightDirection = new Vector3(-1f, 1f, 1.1f).normalized;
    private static readonly Color OutlineColour = new Color32(36, 26, 20, 255);

    private enum Finish { Grain, Wrap, Metal, Plain }

    private sealed class Stuff
    {
        public readonly Color Dark;
        public readonly Color Mid;
        public readonly Color Light;
        public readonly Finish Finish;

        public Stuff(Color32 dark, Color32 mid, Color32 light, Finish finish)
        {
            Dark = dark;
            Mid = mid;
            Light = light;
            Finish = finish;
        }
    }

    private static readonly Stuff Wood = new Stuff(new Color32(84, 52, 30, 255), new Color32(146, 96, 56, 255), new Color32(206, 156, 100, 255), Finish.Grain);
    private static readonly Stuff Leather = new Stuff(new Color32(44, 30, 24, 255), new Color32(92, 62, 46, 255), new Color32(140, 102, 76, 255), Finish.Wrap);
    private static readonly Stuff Steel = new Stuff(new Color32(64, 74, 86, 255), new Color32(138, 150, 164, 255), new Color32(226, 233, 240, 255), Finish.Metal);
    private static readonly Stuff DarkSteel = new Stuff(new Color32(38, 44, 52, 255), new Color32(84, 94, 106, 255), new Color32(160, 170, 182, 255), Finish.Metal);
    private static readonly Stuff Brass = new Stuff(new Color32(110, 78, 30, 255), new Color32(196, 150, 64, 255), new Color32(250, 222, 140, 255), Finish.Metal);
    private static readonly Stuff Cord = new Stuff(new Color32(150, 138, 116, 255), new Color32(216, 206, 184, 255), new Color32(250, 246, 234, 255), Finish.Plain);
    private static readonly Stuff Feather = new Stuff(new Color32(110, 30, 28, 255), new Color32(184, 60, 48, 255), new Color32(236, 120, 96, 255), Finish.Plain);
    private static readonly Stuff WhiteFeather = new Stuff(new Color32(150, 146, 136, 255), new Color32(214, 210, 200, 255), new Color32(250, 250, 246, 255), Finish.Plain);

    public static Texture2D Paint(int item)
    {
        List<Shape> shapes = Design(item);
        Fit(shapes);

        var colours = new Color[Size * Size];
        foreach (Shape shape in shapes) shape.FindBox();

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Color sum = Color.clear;
                for (int sy = 0; sy < Supersample; sy++)
                {
                    for (int sx = 0; sx < Supersample; sx++)
                    {
                        var p = new Vector2(x + (sx + 0.5f) / Supersample, y + (sy + 0.5f) / Supersample);
                        Color c = ColourAt(shapes, p);
                        sum += new Color(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
                    }
                }

                colours[x + Size * y] = sum / (Supersample * Supersample);
            }
        }

        var icon = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        icon.SetPixels(UnderShadow(colours));
        icon.Apply();
        return icon;
    }

    // Shapes in their order, later ones on top; each draws an outline ring around itself, thinner
    // where it lies over a shape behind it.
    private static Color ColourAt(List<Shape> shapes, Vector2 p)
    {
        Color colour = Color.clear;
        bool filled = false;
        foreach (Shape shape in shapes)
        {
            if (!shape.MayContain(p)) continue;

            if (shape.Contains(p, 0f))
            {
                colour = shape.ColourAt(p);
                filled = true;
                continue;
            }

            float ring = filled ? Mathf.Min(InnerOutline, shape.Outline) : shape.Outline;
            if (ring > 0f && shape.Contains(p, ring))
            {
                colour = OutlineColour;
                filled = true;
            }
        }

        return colour;
    }

    // Premultiplied colours in, straight colours out, over a blurred shadow of the icon.
    private static Color[] UnderShadow(Color[] premultiplied)
    {
        var shadow = new float[Size * Size];
        int dx = Mathf.RoundToInt(ShadowOffset.x);
        int dy = Mathf.RoundToInt(ShadowOffset.y);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int sx = x - dx;
                int sy = y - dy;
                if (sx >= 0 && sy >= 0 && sx < Size && sy < Size) shadow[x + Size * y] = premultiplied[sx + Size * sy].a;
            }
        }

        shadow = Blur(Blur(shadow, 2), 2);
        var result = new Color[Size * Size];
        for (int i = 0; i < result.Length; i++)
        {
            Color c = premultiplied[i];
            float s = shadow[i] * ShadowAlpha * (1f - c.a);
            float alpha = c.a + s;
            result[i] = alpha > 0.001f ? new Color(c.r / alpha, c.g / alpha, c.b / alpha, alpha) : Color.clear;
        }

        return result;
    }

    private static float[] Blur(float[] values, int radius)
    {
        var across = new float[values.Length];
        var result = new float[values.Length];
        float count = 2 * radius + 1;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++) sum += values[Mathf.Clamp(x + k, 0, Size - 1) + Size * y];
                across[x + Size * y] = sum / count;
            }
        }

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float sum = 0f;
                for (int k = -radius; k <= radius; k++) sum += across[x + Size * Mathf.Clamp(y + k, 0, Size - 1)];
                result[x + Size * y] = sum / count;
            }
        }

        return result;
    }

    // Turns the design a eighth of a turn clockwise (its +y toward the upper right), then scales and
    // centres it so that it fills the icon within the margin.
    private static void Fit(List<Shape> shapes)
    {
        foreach (Shape shape in shapes) shape.Map(p => new Vector2(0.70710678f * (p.x + p.y), 0.70710678f * (p.y - p.x)), 1f);

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (Shape shape in shapes) shape.Extend(ref min, ref max);

        float scale = (Size - 2f * Margin - 2f * OuterOutline) / Mathf.Max(max.x - min.x, max.y - min.y);
        Vector2 middle = (min + max) * 0.5f;
        var centre = new Vector2(Size * 0.5f - ShadowOffset.x * 0.5f, Size * 0.5f - ShadowOffset.y * 0.5f);
        foreach (Shape shape in shapes) shape.Map(p => centre + (p - middle) * scale, scale);
    }

    // Lightness 0..1 to a colour on the stuff's ramp; bright metal goes toward white.
    private static Color Ramp(Stuff stuff, float shade)
    {
        Color c = shade < 0.55f
            ? Color.Lerp(stuff.Dark, stuff.Mid, Mathf.InverseLerp(0.15f, 0.55f, shade))
            : Color.Lerp(stuff.Mid, stuff.Light, Mathf.InverseLerp(0.55f, 0.85f, shade));
        if (stuff.Finish == Finish.Metal && shade > 0.85f) c = Color.Lerp(c, Color.white, Mathf.InverseLerp(0.85f, 1f, shade) * 0.6f);
        c.a = 1f;
        return c;
    }

    private static float Lit(Vector3 normal)
    {
        return 0.15f + 0.85f * Mathf.Max(0f, Vector3.Dot(normal.normalized, LightDirection));
    }

    private static float WithFinish(Finish finish, float shade, float along, float across)
    {
        switch (finish)
        {
            case Finish.Grain:
                return shade + (Mathf.PerlinNoise(along * 0.09f + 3.1f, across * 0.4f + 7.3f) - 0.5f) * 0.4f;
            case Finish.Wrap:
                return Mathf.Repeat((along + across * 1.3f) / 4.5f, 1f) < 0.3f ? shade * 0.55f : shade;
            default:
                return shade;
        }
    }

    private abstract class Shape
    {
        public Stuff Stuff;
        public float Outline = OuterOutline;
        private Vector2 boxMin;
        private Vector2 boxMax;

        public abstract void Map(Func<Vector2, Vector2> place, float scale);
        public abstract void Extend(ref Vector2 min, ref Vector2 max);
        public abstract bool Contains(Vector2 p, float grow);
        public abstract Color ColourAt(Vector2 p);

        public void FindBox()
        {
            boxMin = new Vector2(float.MaxValue, float.MaxValue);
            boxMax = new Vector2(float.MinValue, float.MinValue);
            Extend(ref boxMin, ref boxMax);
            float grow = OuterOutline + 1f;
            boxMin -= new Vector2(grow, grow);
            boxMax += new Vector2(grow, grow);
        }

        public bool MayContain(Vector2 p)
        {
            return p.x >= boxMin.x && p.y >= boxMin.y && p.x <= boxMax.x && p.y <= boxMax.y;
        }

        protected static void Include(ref Vector2 min, ref Vector2 max, Vector2 p, float grow)
        {
            min = Vector2.Min(min, p - new Vector2(grow, grow));
            max = Vector2.Max(max, p + new Vector2(grow, grow));
        }
    }

    // A straight part with square ends, round in section.
    private sealed class Rod : Shape
    {
        private Vector2 a;
        private Vector2 b;
        private float half;

        public Rod(Stuff stuff, float ax, float ay, float bx, float by, float halfWidth)
        {
            Stuff = stuff;
            a = new Vector2(ax, ay);
            b = new Vector2(bx, by);
            half = halfWidth;
        }

        public override void Map(Func<Vector2, Vector2> place, float scale)
        {
            a = place(a);
            b = place(b);
            half *= scale;
        }

        public override void Extend(ref Vector2 min, ref Vector2 max)
        {
            Include(ref min, ref max, a, half);
            Include(ref min, ref max, b, half);
        }

        private void Frame(Vector2 p, out float along, out float across, out float length, out Vector2 normal)
        {
            Vector2 d = b - a;
            length = d.magnitude;
            d /= length;
            normal = new Vector2(-d.y, d.x);
            along = Vector2.Dot(p - a, d);
            across = Vector2.Dot(p - a, normal);
        }

        public override bool Contains(Vector2 p, float grow)
        {
            Frame(p, out float along, out float across, out float length, out _);
            return along >= -grow && along <= length + grow && Mathf.Abs(across) <= half + grow;
        }

        public override Color ColourAt(Vector2 p)
        {
            Frame(p, out float along, out float across, out _, out Vector2 normal);
            float t = Mathf.Clamp(across / half, -1f, 1f);
            float shade = Lit(new Vector3(normal.x * t, normal.y * t, Mathf.Sqrt(1f - t * t)));
            return Ramp(Stuff, WithFinish(Stuff.Finish, shade, along, across));
        }
    }

    // A bent part: a line through points, round in section, its half width given at each point.
    private sealed class Curve : Shape
    {
        private readonly Vector2[] points;
        private readonly float[] halves;

        public Curve(Stuff stuff, Vector2[] points, float[] halfWidths)
        {
            Stuff = stuff;
            this.points = points;
            halves = halfWidths;
        }

        public override void Map(Func<Vector2, Vector2> place, float scale)
        {
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = place(points[i]);
                halves[i] *= scale;
            }
        }

        public override void Extend(ref Vector2 min, ref Vector2 max)
        {
            for (int i = 0; i < points.Length; i++) Include(ref min, ref max, points[i], halves[i]);
        }

        // The segment nearest to p, by its distance beyond the curve's surface.
        private float Nearest(Vector2 p, out float across, out float half, out float along, out Vector2 normal)
        {
            float best = float.MaxValue;
            across = 0f;
            half = 1f;
            along = 0f;
            normal = Vector2.up;
            float travelled = 0f;
            for (int i = 0; i + 1 < points.Length; i++)
            {
                Vector2 d = points[i + 1] - points[i];
                float length = d.magnitude;
                d /= length;
                float u = Mathf.Clamp(Vector2.Dot(p - points[i], d), 0f, length);
                float h = Mathf.Lerp(halves[i], halves[i + 1], u / length);
                Vector2 n = new Vector2(-d.y, d.x);
                float gap = Vector2.Distance(p, points[i] + d * u) - h;
                if (gap < best)
                {
                    best = gap;
                    across = Vector2.Dot(p - points[i], n);
                    half = h;
                    along = travelled + u;
                    normal = n;
                }

                travelled += length;
            }

            return best;
        }

        public override bool Contains(Vector2 p, float grow)
        {
            return Nearest(p, out _, out _, out _, out _) <= grow;
        }

        public override Color ColourAt(Vector2 p)
        {
            Nearest(p, out float across, out float half, out float along, out Vector2 normal);
            float t = Mathf.Clamp(across / half, -1f, 1f);
            float shade = Lit(new Vector3(normal.x * t, normal.y * t, Mathf.Sqrt(1f - t * t)));
            return Ramp(Stuff, WithFinish(Stuff.Finish, shade, along, across));
        }
    }

    // A flat part (a blade, a plate) facing the viewer, its edges bevelled toward the light.
    private sealed class Plate : Shape
    {
        private readonly Vector2[] points;
        private Vector2 centre;
        private float radius;

        public Plate(Stuff stuff, params Vector2[] points)
        {
            Stuff = stuff;
            this.points = points;
        }

        public override void Map(Func<Vector2, Vector2> place, float scale)
        {
            for (int i = 0; i < points.Length; i++) points[i] = place(points[i]);

            centre = Vector2.zero;
            foreach (Vector2 point in points) centre += point / points.Length;
            radius = 0.01f;
            foreach (Vector2 point in points) radius = Mathf.Max(radius, Vector2.Distance(point, centre));
        }

        public override void Extend(ref Vector2 min, ref Vector2 max)
        {
            foreach (Vector2 point in points) Include(ref min, ref max, point, 0f);
        }

        private bool Inside(Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }

            return inside;
        }

        // The distance to the nearest edge and that edge's outward normal.
        private float NearestEdge(Vector2 p, out Vector2 outward)
        {
            float area = 0f;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++) area += points[j].x * points[i].y - points[i].x * points[j].y;

            float best = float.MaxValue;
            outward = Vector2.zero;
            for (int i = 0, j = points.Length - 1; i < points.Length; j = i++)
            {
                Vector2 a = points[j];
                Vector2 b = points[i];
                Vector2 d = b - a;
                float length = d.magnitude;
                if (length < 1e-4f) continue;

                d /= length;
                float u = Mathf.Clamp(Vector2.Dot(p - a, d), 0f, length);
                float distance = Vector2.Distance(p, a + d * u);
                if (distance < best)
                {
                    best = distance;
                    // Counter-clockwise outlines (positive area) have their outside on the right.
                    outward = area > 0f ? new Vector2(d.y, -d.x) : new Vector2(-d.y, d.x);
                }
            }

            return best;
        }

        public override bool Contains(Vector2 p, float grow)
        {
            if (Inside(p)) return true;
            return grow > 0f && NearestEdge(p, out _) <= grow;
        }

        public override Color ColourAt(Vector2 p)
        {
            float edge = NearestEdge(p, out Vector2 outward);
            float tilt = edge < Bevel ? (1f - edge / Bevel) * 1.4f : 0f;
            float shade = Lit(new Vector3(outward.x * tilt, outward.y * tilt, 1f));
            shade += 0.18f * Vector2.Dot((p - centre) / radius, new Vector2(LightDirection.x, LightDirection.y).normalized);
            return Ramp(Stuff, WithFinish(Stuff.Finish, shade, p.x, p.y));
        }
    }

    // A round stud seen from the front.
    private sealed class Disc : Shape
    {
        private Vector2 centre;
        private float radius;

        public Disc(Stuff stuff, float x, float y, float radius)
        {
            Stuff = stuff;
            centre = new Vector2(x, y);
            this.radius = radius;
            Outline = 0.8f;
        }

        public override void Map(Func<Vector2, Vector2> place, float scale)
        {
            centre = place(centre);
            radius *= scale;
        }

        public override void Extend(ref Vector2 min, ref Vector2 max)
        {
            Include(ref min, ref max, centre, radius);
        }

        public override bool Contains(Vector2 p, float grow)
        {
            return Vector2.Distance(p, centre) <= radius + grow;
        }

        public override Color ColourAt(Vector2 p)
        {
            Vector2 v = (p - centre) / radius;
            float z = Mathf.Sqrt(Mathf.Max(0f, 1f - v.sqrMagnitude));
            return Ramp(Stuff, Lit(new Vector3(v.x, v.y, z)));
        }
    }

    private static Vector2 V(float x, float y) => new Vector2(x, y);

    // Each design in its own frame: the handle along +y from about -62, the working end up, the front
    // (blade, edge) toward -x, which the icon turns toward the upper left, into the light.
    private static List<Shape> Design(int item)
    {
        switch (item)
        {
            case Items.Shovel:
                return new List<Shape>
                {
                    new Rod(Wood, 0f, -58f, 0f, 34f, 4.5f),
                    new Rod(Wood, -13f, -62f, 13f, -62f, 4.5f),
                    new Plate(DarkSteel, V(-5f, 26f), V(5f, 26f), V(7.5f, 40f), V(-7.5f, 40f)),
                    new Disc(Brass, 0f, 32f, 1.8f),
                    new Plate(Steel, V(-17f, 38f), V(17f, 38f), V(18f, 58f), V(0f, 76f), V(-18f, 58f)),
                    new Rod(DarkSteel, 0f, 42f, 0f, 64f, 1.1f) { Outline = 0f }
                };

            case Items.Pickaxe:
            {
                // A curved head pointed at both ends.
                var head = new List<Vector2>();
                const float reach = 50f;
                for (int i = 0; i <= 12; i++)
                {
                    float x = -reach + 2f * reach * i / 12f;
                    head.Add(V(x, 60f - 0.0065f * x * x));
                }

                for (int i = 12; i >= 0; i--)
                {
                    float x = -reach + 2f * reach * i / 12f;
                    float share = Mathf.Abs(x) / reach;
                    float thickness = 12f * Mathf.Pow(Mathf.Max(0f, 1f - share * share), 0.6f) + 0.8f;
                    head.Add(V(x, 60f - 0.0065f * x * x - thickness));
                }

                return new List<Shape>
                {
                    new Rod(Wood, 0f, -62f, 0f, 52f, 4.5f),
                    new Rod(Leather, 0f, -62f, 0f, -38f, 5.6f),
                    new Plate(Steel, head.ToArray()),
                    new Plate(DarkSteel, V(-7f, 45f), V(7f, 45f), V(7f, 63f), V(-7f, 63f)),
                    new Disc(Brass, 0f, 54f, 2f)
                };
            }

            case Items.Axe:
                return new List<Shape>
                {
                    new Rod(Wood, 0f, -62f, 0f, 70f, 4.5f),
                    new Rod(Leather, 0f, -62f, 0f, -40f, 5.6f),
                    new Plate(Steel,
                        V(10f, 46f), V(8f, 40f), V(-4f, 39f), V(-16f, 41f), V(-30f, 31f), V(-40f, 22f), V(-44f, 36f), V(-46f, 52f),
                        V(-44f, 68f), V(-40f, 82f), V(-30f, 73f), V(-16f, 63f), V(-4f, 65f), V(8f, 64f), V(10f, 58f)),
                    new Disc(Brass, 2f, 52f, 2f)
                };

            case Items.Scythe:
            {
                // The blade: its spine curving down to the tip, its edge below, narrowing to a point.
                var blade = new List<Vector2>();
                for (int i = 0; i <= 12; i++)
                {
                    float s = i / 12f;
                    blade.Add(V(3f - 66f * s, 66f - 26f * s * s));
                }

                for (int i = 12; i >= 0; i--)
                {
                    float s = i / 12f;
                    float width = 13f * Mathf.Pow(1f - s, 0.7f) + 0.4f;
                    blade.Add(V(3f - 66f * s, 66f - 26f * s * s - width));
                }

                return new List<Shape>
                {
                    new Rod(Wood, 0f, -62f, 0f, 62f, 4f),
                    new Rod(Leather, 0f, -62f, 0f, -46f, 5f),
                    new Rod(Wood, 0f, -10f, 13f, -10f, 3.2f),
                    new Plate(Steel, blade.ToArray()),
                    new Plate(DarkSteel, V(-6f, 50f), V(6f, 50f), V(6f, 67f), V(-6f, 67f)),
                    new Disc(Brass, 0f, 58f, 2f)
                };
            }

            case Items.Hoe:
                return new List<Shape>
                {
                    new Rod(Wood, 0f, -62f, 0f, 60f, 4.5f),
                    new Rod(Leather, 0f, -62f, 0f, -40f, 5.6f),
                    new Plate(Steel, V(-5f, 63f), V(-20f, 63f), V(-26f, 58f), V(-33f, 26f), V(-11f, 26f), V(-17f, 52f), V(-5f, 53f)),
                    new Plate(DarkSteel, V(-6f, 50f), V(6f, 50f), V(6f, 66f), V(-6f, 66f)),
                    new Disc(Brass, 0f, 58f, 2f)
                };

            case Items.Bow:
            {
                var limb = new List<Vector2>();
                var halves = new List<float>();
                for (int i = 0; i <= 20; i++)
                {
                    float t = -1f + 2f * i / 20f;
                    limb.Add(V(-30f * (1f - t * t), 64f * t));
                    halves.Add(2.2f + 2.8f * (1f - Mathf.Abs(t)));
                }

                var grip = new List<Vector2>();
                var gripHalves = new List<float>();
                for (int i = 0; i <= 4; i++)
                {
                    float t = -0.14f + 0.28f * i / 4f;
                    grip.Add(V(-30f * (1f - t * t), 64f * t));
                    gripHalves.Add(5.8f);
                }

                return new List<Shape>
                {
                    new Rod(Cord, 0f, -64f, 0f, 64f, 0.9f) { Outline = 1f },
                    new Curve(Wood, limb.ToArray(), halves.ToArray()),
                    new Curve(Leather, grip.ToArray(), gripHalves.ToArray())
                };
            }

            case Items.Knife:
                // A leather-wrapped handle, a guard, and a long blade, its edge toward -x curving up to the point.
                return new List<Shape>
                {
                    new Rod(Wood, 0f, -62f, 0f, -8f, 6f),
                    new Rod(Leather, 0f, -54f, 0f, -16f, 6.8f),
                    new Disc(Brass, 0f, -60f, 3.4f),
                    new Plate(Steel, V(6f, -2f), V(6f, 46f), V(2f, 62f), V(-4f, 72f), V(-9f, 52f), V(-11f, 26f), V(-10f, -2f)),
                    new Rod(DarkSteel, 2.5f, 4f, 2.5f, 50f, 1f) { Outline = 0f },
                    new Plate(DarkSteel, V(-15f, -10f), V(13f, -10f), V(13f, -3f), V(-15f, -3f)),
                    new Disc(Brass, 0f, -30f, 1.8f)
                };

            case Items.Arrow:
                return new List<Shape>
                {
                    new Rod(Wood, 0f, -60f, 0f, 50f, 2.6f),
                    new Plate(Feather, V(-1.5f, -36f), V(-1.5f, -58f), V(-11f, -65f), V(-11f, -45f)),
                    new Plate(WhiteFeather, V(1.5f, -36f), V(11f, -45f), V(11f, -65f), V(1.5f, -58f)),
                    new Rod(Leather, 0f, -65f, 0f, -59f, 3.4f),
                    new Plate(Steel, V(0f, 74f), V(-9.5f, 46f), V(0f, 51f), V(9.5f, 46f))
                };

            default:
                return new List<Shape>();
        }
    }
}
