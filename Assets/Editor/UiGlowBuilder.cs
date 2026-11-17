using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Writes the UI's glows (menu Eco > Write UI glow), soft white light for UI Toolkit, which has no
// shadows; USS tints them (`-unity-background-image-tint-color`). The skills tab lights up with them
// what is under way in green and what is complete in gold.
// - Assets/UI/Icons/Glow.png: around a rounded square, laid behind an element with 9-slice borders of
//   SliceBorder (`-unity-slice-*`), so it stretches to any size: an upgrade's icon frame.
// - Assets/UI/Icons/GlowRound.png: around a disc of RoundShare of the texture's width, stretched
//   behind something round: a tier's disc, a bought pip.
public static class UiGlowBuilder
{
    public const int SliceBorder = 24;
    public const float RoundShare = 0.625f;
    private const int Size = 64;
    private const float Inner = 22f;      // the solid square's half size
    private const float Corner = 6f;      // its corners' radius
    private const float Falloff = 5f;     // how fast the glow fades outside it (10 pixels to the edge)
    private const float RoundFalloff = 5.5f;
    private const string GlowPath = "Assets/UI/Icons/Glow.png";
    private const string RoundPath = "Assets/UI/Icons/GlowRound.png";

    [MenuItem("Eco/Write UI glow")]
    public static void Write()
    {
        const float centre = Size * 0.5f;
        WriteGlow(GlowPath, (x, y) =>
        {
            // Distance from the rounded square, 0 inside it.
            float dx = Mathf.Max(Mathf.Abs(x - centre) - (Inner - Corner), 0f);
            float dy = Mathf.Max(Mathf.Abs(y - centre) - (Inner - Corner), 0f);
            float outside = Mathf.Max(Mathf.Sqrt(dx * dx + dy * dy) - Corner, 0f);
            return Mathf.Exp(-(outside * outside) / (Falloff * Falloff));
        });

        WriteGlow(RoundPath, (x, y) =>
        {
            float outside = Mathf.Max(Vector2.Distance(new Vector2(x, y), new Vector2(centre, centre)) - centre * RoundShare, 0f);
            return Mathf.Exp(-(outside * outside) / (RoundFalloff * RoundFalloff));
        });
    }

    private static void WriteGlow(string path, Func<float, float, float> alphaAt)
    {
        var pixels = new Color[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                // Nothing on the outermost pixels, so a stretched glow never shows a hard edge.
                bool edge = x == 0 || y == 0 || x == Size - 1 || y == Size - 1;
                pixels[x + Size * y] = new Color(1f, 1f, 1f, edge ? 0f : alphaAt(x + 0.5f, y + 0.5f));
            }
        }

        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
        texture.SetPixels(pixels);
        texture.Apply();
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        // The import settings of the other UI icons.
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
}
