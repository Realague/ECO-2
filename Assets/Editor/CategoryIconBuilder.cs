using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Writes the inventory category icons the user's mockup lacks (menu Eco > Write category icons),
// white on transparent so that USS tints them: Assets/UI/Icons/CategorySeeds.png, a solid sprout,
// and CategoryMaterials.png, a tied sheaf (Materials holds straw and carcasses since wood has a
// category of its own). The other category icons (CategoryAll, the bag; CategoryTools,
// CategoryBlocks, CategoryFood, and CategoryWood, the logs that were Materials' icon) were cut from
// the mockup; these are drawn to match them. It also writes the Skills tab's glyph, TabSkills.png,
// an open book (also beside the skill points). Supersampled four times.
public static class CategoryIconBuilder
{
    private const int Size = 128;
    private const int Supersample = 4;
    private const string SeedsPath = "Assets/UI/Icons/CategorySeeds.png";
    private const string MaterialsPath = "Assets/UI/Icons/CategoryMaterials.png";
    private const string SkillsPath = "Assets/UI/Icons/TabSkills.png";

    [MenuItem("Eco/Write category icons")]
    public static void Write()
    {
        // A sprout: two leaves on a stem, over a mound of earth.
        WriteIcon(SeedsPath, new List<Func<Vector2, bool>>
        {
            p => p.y >= 14 && Ellipse(p, 64, 14, 46, 20, 0),
            p => Capsule(p, new Vector2(64, 16), new Vector2(64, 66), 5f),
            p => Ellipse(p, 38, 78, 30, 15, 35),
            p => Ellipse(p, 90, 88, 34, 17, -35)
        });

        WriteIcon(MaterialsPath, SheafParts());
        WriteIcon(SkillsPath, new List<Func<Vector2, bool>> { OpenBook });
    }

    // An open book seen from the front: two pages dipping toward the spine, a few lines of text cut
    // out of each, over the cover's edge, set apart from the pages by a thin gap.
    private static bool OpenBook(Vector2 p)
    {
        const float spine = 64f;
        const float halfGap = 3f;
        const float pageWidth = 50f;
        float fromSpine = Mathf.Abs(p.x - spine);
        float t = 1f - Mathf.Clamp01((fromSpine - halfGap) / pageWidth);
        float dip = 9f * t * t;
        float top = 100f - dip;
        float bottom = 32f - dip;

        if (fromSpine >= halfGap && fromSpine <= halfGap + pageWidth && p.y >= bottom && p.y <= top)
        {
            for (int line = 1; line <= 4; line++)
            {
                float lineY = top - 4f - line * 12f;
                if (fromSpine >= halfGap + 7f && fromSpine <= halfGap + pageWidth - 8f && Mathf.Abs(p.y - lineY) < 2f) return false;
            }

            return true;
        }

        // The cover: a band under the pages, a little wider, meeting at the spine.
        return fromSpine <= halfGap + pageWidth + 5f && p.y >= bottom - 11f && p.y <= bottom - 4f;
    }

    // A sheaf: seven stalks drawn together at a band and fanning out above and below it, an ear on
    // each, the band set apart from them by a thin gap.
    private static List<Func<Vector2, bool>> SheafParts()
    {
        const float tieY = 54f;
        var parts = new List<Func<Vector2, bool>>
        {
            p => Capsule(p, new Vector2(42, tieY), new Vector2(86, tieY), 6f)
        };

        for (int i = -3; i <= 3; i++)
        {
            var bottom = new Vector2(64 + i * 6.5f, 10);
            var tie = new Vector2(64 + i * 2.6f, tieY);
            var top = new Vector2(64 + i * 8.5f, 94);
            parts.Add(p => Mathf.Abs(p.y - tieY) > 9f && (Capsule(p, bottom, tie, 2.8f) || Capsule(p, tie, top, 2.8f)));

            float degrees = Mathf.Atan2(top.y - tie.y, top.x - tie.x) * Mathf.Rad2Deg;
            Vector2 ear = top + (top - tie).normalized * 8f;
            parts.Add(p => Ellipse(p, ear.x, ear.y, 11f, 5f, degrees));
        }

        return parts;
    }

    private static void WriteIcon(string path, List<Func<Vector2, bool>> parts)
    {
        var pixels = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < Supersample; sy++)
                {
                    for (int sx = 0; sx < Supersample; sx++)
                    {
                        var p = new Vector2(x + (sx + 0.5f) / Supersample, y + (sy + 0.5f) / Supersample);
                        if (parts.Exists(inside => inside(p))) covered++;
                    }
                }

                pixels[x + Size * y] = new Color(1f, 1f, 1f, covered / (float)(Supersample * Supersample));
            }
        }

        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        texture.SetPixels(pixels);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        // The import settings of the other icons.
        AssetDatabase.ImportAsset(path);
        if (AssetImporter.GetAtPath("Assets/UI/Icons/Plant.png") is TextureImporter reference && AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            var settings = new TextureImporterSettings();
            reference.ReadTextureSettings(settings);
            importer.SetTextureSettings(settings);
            importer.textureCompression = reference.textureCompression;
            importer.SaveAndReimport();
        }

        Debug.Log($"Wrote {path}.");
    }

    // Inside an ellipse turned by degrees anticlockwise, in icon pixels with y up.
    private static bool Ellipse(Vector2 p, float x, float y, float halfLength, float halfWidth, float degrees)
    {
        float c = Mathf.Cos(degrees * Mathf.Deg2Rad);
        float s = Mathf.Sin(degrees * Mathf.Deg2Rad);
        Vector2 d = p - new Vector2(x, y);
        float u = (d.x * c + d.y * s) / halfLength;
        float v = (-d.x * s + d.y * c) / halfWidth;
        return u * u + v * v <= 1f;
    }

    private static bool Capsule(Vector2 p, Vector2 a, Vector2 b, float radius)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return (p - (a + ab * t)).sqrMagnitude <= radius * radius;
    }
}
