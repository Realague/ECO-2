using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Writes the inventory's Seeds category icon (menu Eco > Write category icons) into
// Assets/UI/Icons/CategorySeeds.png: a solid sprout, white on transparent so that USS tints it. The
// other category icons (CategoryAll, the bag; CategoryTools, CategoryBlocks, CategoryFood,
// CategoryMaterials) were cut from the user's inventory mockup, which has no seeds; this one is
// drawn to match them. Supersampled four times.
public static class CategoryIconBuilder
{
    private const int Size = 128;
    private const int Supersample = 4;
    private const string Path = "Assets/UI/Icons/CategorySeeds.png";

    [MenuItem("Eco/Write category icons")]
    public static void Write()
    {
        // A sprout: two leaves on a stem, over a mound of earth.
        var parts = new List<Func<Vector2, bool>>
        {
            p => p.y >= 14 && Ellipse(p, 64, 14, 46, 20, 0),
            p => Capsule(p, new Vector2(64, 16), new Vector2(64, 66), 5f),
            p => Ellipse(p, 38, 78, 30, 15, 35),
            p => Ellipse(p, 90, 88, 34, 17, -35)
        };

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
        File.WriteAllBytes(Path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        // The import settings of the other icons.
        AssetDatabase.ImportAsset(Path);
        if (AssetImporter.GetAtPath("Assets/UI/Icons/Plant.png") is TextureImporter reference && AssetImporter.GetAtPath(Path) is TextureImporter importer)
        {
            var settings = new TextureImporterSettings();
            reference.ReadTextureSettings(settings);
            importer.SetTextureSettings(settings);
            importer.textureCompression = reference.textureCompression;
            importer.SaveAndReimport();
        }

        Debug.Log($"Wrote {Path}.");
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
