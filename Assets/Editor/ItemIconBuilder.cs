using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Voxel.Client;
using Voxel.Core;

// Writes the item icons the inventory shows (menu Eco > Rebuild item icons), one PNG of IconSize
// pixels per item in Assets/Resources/ItemIcons, loaded at run time by ItemIcons:
// - a block (Block_<type>.png) is a cube seen from above at three quarters, painted pixel by pixel
//   from its tiles in Assets/Resources/Blocks (BlockAtlas.TileFor), grass tinted a mild green;
// - a plant (Plant_<species>.png, used for seeds and for crops without fruit) is its first model
//   rendered by a camera in a preview scene, on black then on white to recover the transparency;
// - a seed (Seed_<species>.png) is a pouch tinted like the plant with the plant in front of it;
// - a crop (Produce_<species>.png) is a pile of fruit of its model's last stage, or the plant;
// - each tree's wood (Wood_<asset>.png) is a pile of logs in its bark, straw (Straw.png) a sheaf;
// - tools and the arrow (Tool_<name>.png, Arrow.png) are painted as illustrations (ToolIconPainter).
// Species are found by asset in Assets/Species and named after the asset, so the icons do not depend
// on the order of species. Every file is written again; rerun the menu after adding a species.
public static class ItemIconBuilder
{
    public const int IconSize = 128;
    private const string Folder = "Assets/Resources/ItemIcons";
    private const string BlockFolder = "Assets/Resources/Blocks";

    // BlockAtlas.TileFor's faces: the top and a side (ChunkMesher.TopFace, face 0).
    private const int TopFace = 2;
    private const int SideFace = 0;

    [MenuItem("Eco/Rebuild item icons")]
    public static void Rebuild()
    {
        Directory.CreateDirectory(Folder);
        int written = 0;

        for (int type = 1; type <= Blocks.LastType; type++)
        {
            if (!Items.IsBlock(type)) continue;

            Texture2D block = BlockIcon((ushort)type);
            Write($"Block_{type}", block);
            UnityEngine.Object.DestroyImmediate(block);
            written++;
        }

        var renderer = new PreviewRenderer();
        try
        {
            foreach (string guid in AssetDatabase.FindAssets("t:SpeciesAsset", new[] { "Assets/Species" }))
            {
                var asset = AssetDatabase.LoadAssetAtPath<SpeciesAsset>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null || asset.ModelCount == 0) continue;

                Texture2D plant = renderer.Render(asset.PartsOf(0), 25f, 1.05f);
                Texture2D seed = SeedIcon(plant, MainColour(asset.PartsOf(0)));
                Texture2D produce = ProduceIcon(renderer, asset);
                Write($"Plant_{asset.name}", plant);
                Write($"Seed_{asset.name}", seed);
                Write($"Produce_{asset.name}", produce ?? plant);
                if (asset.WoodYield > 0)
                {
                    Texture2D wood = renderer.Render(LogParts(asset.BarkMaterial != null ? SpeciesAsset.WoodMaterialFrom(asset.BarkMaterial, asset.WoodColour) : null), 20f, 1.1f);
                    Write($"Wood_{asset.name}", wood);
                    UnityEngine.Object.DestroyImmediate(wood);
                    written++;
                }

                UnityEngine.Object.DestroyImmediate(seed);
                if (produce != null) UnityEngine.Object.DestroyImmediate(produce);
                UnityEngine.Object.DestroyImmediate(plant);
                written += 3;
            }

            Texture2D straw = renderer.Render(SheafParts(), 20f, 1.1f);
            Write("Straw", straw);
            UnityEngine.Object.DestroyImmediate(straw);
            written++;

            foreach (int tool in Items.Tools)
            {
                Texture2D icon = ToolIconPainter.Paint(tool);
                Write($"Tool_{Items.NameOf(tool)}", icon);
                UnityEngine.Object.DestroyImmediate(icon);
                written++;
            }

            Texture2D arrow = ToolIconPainter.Paint(Items.Arrow);
            Write("Arrow", arrow);
            UnityEngine.Object.DestroyImmediate(arrow);
            written++;

            Texture2D chest = renderer.Render(ChestParts(), 22f, 1.0f);
            Write("Chest", chest);
            UnityEngine.Object.DestroyImmediate(chest);
            Texture2D stockpile = renderer.Render(StockpileParts(), 32f, 0.95f);
            Write("Stockpile", stockpile);
            UnityEngine.Object.DestroyImmediate(stockpile);
            written += 2;
        }
        finally
        {
            renderer.Dispose();
        }

        AssetDatabase.Refresh();
        Debug.Log($"Wrote {written} item icons to {Folder}.");
    }

    private static void Write(string name, Texture2D icon)
    {
        string path = $"{Folder}/{name}.png";
        File.WriteAllBytes(path, icon.EncodeToPNG());
        AssetDatabase.ImportAsset(path);
        if (AssetImporter.GetAtPath(path) is TextureImporter importer && (importer.textureType != TextureImporterType.Sprite || importer.mipmapEnabled))
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
    }

    // A cube seen from above at three quarters: the top as a rhombus, the left and right sides below
    // it, each pixel taken from its face's tile (point sampling, like the blocks in the world).
    private static Texture2D BlockIcon(ushort block)
    {
        Color32[] top = Tile(BlockAtlas.TileFor(block, TopFace), out int topSize, true);
        Color32[] side = Tile(BlockAtlas.TileFor(block, SideFace), out int sideSize, true);
        float alpha = Blocks.TypeOf(block) == Blocks.Water ? 0.8f : 1f;

        var pixels = new Color32[IconSize * IconSize];
        const float edge = 58f;
        float across = edge * Mathf.Cos(30f * Mathf.Deg2Rad);
        float rise = edge * 0.5f;
        var centre = new Vector2(IconSize * 0.5f, IconSize * 0.5f - 2f);
        Vector2 topCorner = centre + new Vector2(0f, edge);
        Vector2 toRight = new Vector2(across, -rise);
        Vector2 toLeft = new Vector2(-across, -rise);
        Vector2 down = new Vector2(0f, -edge);

        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                // Texture rows run upward; faces are worked out in the same frame.
                var point = new Vector2(x + 0.5f, y + 0.5f);
                Color32 colour = default;
                if (Inside(point, topCorner, toRight, toLeft, out float u, out float v))
                {
                    colour = Sample(top, topSize, u, v, 1f);
                }
                else if (Inside(point, centre - toRight, toRight, down, out u, out v))
                {
                    colour = Sample(side, sideSize, u, 1f - v, 0.82f);
                }
                else if (Inside(point, centre, -toLeft, down, out u, out v))
                {
                    colour = Sample(side, sideSize, u, 1f - v, 0.66f);
                }
                else
                {
                    continue;
                }

                colour.a = (byte)(colour.a * alpha);
                pixels[x + IconSize * y] = colour;
            }
        }

        var icon = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
        icon.SetPixels32(pixels);
        icon.Apply();
        return icon;
    }

    // Whether point lies in the parallelogram origin + u * a + v * b (u, v in 0..1).
    private static bool Inside(Vector2 point, Vector2 origin, Vector2 a, Vector2 b, out float u, out float v)
    {
        Vector2 d = point - origin;
        float determinant = a.x * b.y - a.y * b.x;
        u = (d.x * b.y - d.y * b.x) / determinant;
        v = (a.x * d.y - a.y * d.x) / determinant;
        return u >= 0f && u < 1f && v >= 0f && v < 1f;
    }

    private static Color32 Sample(Color32[] tile, int size, float u, float v, float shade)
    {
        Color32 c = tile[Mathf.Clamp((int)(u * size), 0, size - 1) + size * Mathf.Clamp((int)(v * size), 0, size - 1)];
        return new Color32((byte)(c.r * shade), (byte)(c.g * shade), (byte)(c.b * shade), c.a);
    }

    // A tile's pixels, rows upward; with tint, the pixels its mask marks take a mild grass colour.
    private static Color32[] Tile(int tile, out int size, bool tint)
    {
        string name = BlockAtlas.TileNames[tile];
        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{BlockFolder}/{name}.png");
        if (texture == null)
        {
            size = 1;
            return new[] { new Color32(255, 0, 255, 255) };
        }

        size = texture.width;
        Color32[] pixels = texture.GetPixels32();
        var mask = AssetDatabase.LoadAssetAtPath<Texture2D>($"{BlockFolder}/{name}{BlockAtlas.TintSuffix}.png");
        if (tint && mask != null && mask.width == texture.width)
        {
            Color grass = MildGrass();
            Color32[] marks = mask.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                if (marks[i].r < 128) continue;

                Color c = pixels[i];
                pixels[i] = new Color(c.r * grass.r, c.g * grass.g, c.b * grass.b, 1f);
            }
        }

        return pixels;
    }

    // ClimateColours is internal to the game's assembly: its mild grass is read through reflection.
    private static Color MildGrass()
    {
        Type colours = typeof(BlockAtlas).Assembly.GetType("Voxel.Client.ClimateColours");
        object value = colours?.GetProperty("MildGrass")?.GetValue(null);
        return value is Color grass ? grass : new Color(0.45f, 0.7f, 0.3f);
    }

    // A seed pouch tinted like the plant, tied at the neck, with the plant small in front of it.
    private static Texture2D SeedIcon(Texture2D plant, Color tint)
    {
        var pixels = new Color32[IconSize * IconSize];
        Color cloth = Color.Lerp(new Color(0.78f, 0.66f, 0.46f), tint, 0.35f);
        Color tie = new Color(0.45f, 0.3f, 0.18f);
        for (int y = 0; y < IconSize; y++)
        {
            for (int x = 0; x < IconSize; x++)
            {
                float dx = (x - 64f) / 44f;
                float dy = (y - 46f) / 40f;
                bool body = dx * dx + dy * dy <= 1f;
                bool neck = Mathf.Abs(x - 64f) < 14f - (y - 84f) * 0.25f && y >= 80 && y < 100;
                bool lip = Mathf.Abs(x - 64f) < 20f && y >= 100 && y < 108;
                bool band = Mathf.Abs(x - 64f) < 16f && y >= 82 && y < 88;
                if (!(body || neck || lip)) continue;

                float shade = 0.75f + 0.25f * Mathf.Clamp01(1f - dx * 0.6f + dy * 0.3f);
                Color c = band ? tie : cloth * shade;
                c.a = 1f;
                pixels[x + IconSize * y] = c;
            }
        }

        var icon = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
        icon.SetPixels32(pixels);
        Overlay(icon, plant, 0.55f, new Vector2Int(54, 6));
        icon.Apply();
        return icon;
    }

    // Draws source scaled down at an offset over target, by alpha.
    private static void Overlay(Texture2D target, Texture2D source, float scale, Vector2Int offset)
    {
        int size = Mathf.RoundToInt(IconSize * scale);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int tx = x + offset.x;
                int ty = y + offset.y;
                if (tx < 0 || ty < 0 || tx >= IconSize || ty >= IconSize) continue;

                Color s = source.GetPixelBilinear((x + 0.5f) / size, (y + 0.5f) / size);
                if (s.a <= 0f) continue;

                Color t = target.GetPixel(tx, ty);
                Color mixed = Color.Lerp(t, s, s.a);
                mixed.a = Mathf.Max(t.a, s.a);
                target.SetPixel(tx, ty, mixed);
            }
        }
    }

    // A few fruit of the model's last stage of ripeness (the first box of its fruit mesh), or null
    // for a crop that bears none (cereals, roots: their icon is the plant).
    private static Texture2D ProduceIcon(PreviewRenderer renderer, SpeciesAsset asset)
    {
        IReadOnlyList<SpeciesAsset.Part> fruits = asset.FruitPartsOf(0, Eco.Sim.Plant.RipeStageCount);
        if (fruits == null || fruits.Count == 0 || fruits[0].mesh == null) return null;

        Mesh one = FirstBox(fruits[0].mesh);
        if (one == null) return null;

        // A small pile: two side by side and one on top.
        Vector3 size = one.bounds.size;
        Vector3[] places = { new Vector3(-0.55f * size.x, 0f, 0f), new Vector3(0.55f * size.x, 0f, 0.1f * size.z), new Vector3(0f, 0.95f * size.y, 0f) };
        var parts = new SpeciesAsset.Part[places.Length];
        for (int i = 0; i < places.Length; i++)
        {
            parts[i] = new SpeciesAsset.Part { mesh = one, material = fruits[0].material, offset = places[i], scale = Vector3.one };
        }

        Texture2D icon = renderer.Render(parts, 25f, 1.4f);
        UnityEngine.Object.DestroyImmediate(one);
        return icon;
    }

    // The first box of a mesh merged from boxes (24 vertices and 36 indices each), centred.
    private static Mesh FirstBox(Mesh merged)
    {
        if (merged.vertexCount < 24) return null;

        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        merged.GetVertices(vertices);
        merged.GetNormals(normals);
        merged.GetUVs(0, uvs);
        int[] triangles = merged.GetTriangles(0);
        if (triangles.Length < 36) return null;

        var box = new Mesh();
        Vector3 centre = Vector3.zero;
        for (int i = 0; i < 24; i++) centre += vertices[i] / 24f;
        var boxVertices = new Vector3[24];
        for (int i = 0; i < 24; i++) boxVertices[i] = vertices[i] - centre;
        box.vertices = boxVertices;
        if (normals.Count >= 24) box.normals = normals.GetRange(0, 24).ToArray();
        if (uvs.Count >= 24) box.uv = uvs.GetRange(0, 24).ToArray();
        var boxTriangles = new int[36];
        Array.Copy(triangles, boxTriangles, 36);
        box.triangles = boxTriangles;
        box.RecalculateBounds();
        return box;
    }

    // The colour of the part with the most upward-facing area, as the maps take it.
    private static Color MainColour(IReadOnlyList<SpeciesAsset.Part> parts)
    {
        float best = -1f;
        Color colour = Color.green;
        foreach (SpeciesAsset.Part part in parts)
        {
            if (part.mesh == null || part.material == null) continue;

            Vector3 size = Vector3.Scale(part.mesh.bounds.size, part.scale);
            float area = Mathf.Abs(size.x * size.z);
            if (area <= best) continue;

            best = area;
            colour = part.material.HasProperty("_BaseColor") ? part.material.GetColor("_BaseColor") : part.material.color;
        }

        return colour;
    }

    // Three logs, stacked, in a tree's wood (its bark's grain in its wood's colour; the plain trunk's without one).
    private static IReadOnlyList<SpeciesAsset.Part> LogParts(Material bark)
    {
        Material material = bark != null ? bark : AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PlantTrunk.mat");
        Mesh cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
        var parts = new List<SpeciesAsset.Part>();
        Vector3[] offsets = { new Vector3(-0.32f, 0f, 0f), new Vector3(0.32f, 0f, 0f), new Vector3(0f, 0.55f, 0f) };
        foreach (Vector3 offset in offsets)
        {
            parts.Add(new SpeciesAsset.Part { mesh = RotatedOnItsSide(cylinder), material = material, offset = offset, scale = new Vector3(0.6f, 0.6f, 0.6f) });
        }

        return parts;
    }

    // A closed chest, as placed chests are drawn (ChestShapes), its front toward the camera.
    private static IReadOnlyList<SpeciesAsset.Part> ChestParts()
    {
        var wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/ArrowShaft.mat");
        var iron = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/ArrowHead.mat");
        var parts = new List<SpeciesAsset.Part>();
        AddBoxes(parts, ChestShapes.BodyWood, Vector3.zero, wood);
        AddBoxes(parts, ChestShapes.BodyIron, Vector3.zero, iron);
        AddBoxes(parts, ChestShapes.LidWood, ChestShapes.Hinge, wood);
        AddBoxes(parts, ChestShapes.LidIron, ChestShapes.Hinge, iron);
        return parts;
    }

    // The camera looks toward +z: the chest is mirrored so that its front (+z) faces it.
    private static void AddBoxes(List<SpeciesAsset.Part> parts, IEnumerable<(Vector3 centre, Vector3 size)> boxes, Vector3 origin, Material material)
    {
        Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        foreach ((Vector3 centre, Vector3 size) in boxes)
        {
            Vector3 at = origin + centre;
            parts.Add(new SpeciesAsset.Part { mesh = cube, material = material, offset = new Vector3(at.x, at.y, -at.z), scale = size });
        }
    }

    // A stockpile: a frame of planks around nine cells, piles of small stone and dirt cubes on some.
    private static IReadOnlyList<SpeciesAsset.Part> StockpileParts()
    {
        var plank = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/ArrowShaft.mat");
        var stone = Tinted(new Color(0.55f, 0.55f, 0.57f));
        var dirt = Tinted(new Color(0.47f, 0.33f, 0.22f));
        Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var parts = new List<SpeciesAsset.Part>();
        const float side = 3f;
        const float thick = 0.12f;
        parts.Add(new SpeciesAsset.Part { mesh = cube, material = plank, offset = new Vector3(side * 0.5f, 0.08f, -thick * 0.5f), scale = new Vector3(side + 2f * thick, 0.16f, thick) });
        parts.Add(new SpeciesAsset.Part { mesh = cube, material = plank, offset = new Vector3(side * 0.5f, 0.08f, side + thick * 0.5f), scale = new Vector3(side + 2f * thick, 0.16f, thick) });
        parts.Add(new SpeciesAsset.Part { mesh = cube, material = plank, offset = new Vector3(-thick * 0.5f, 0.08f, side * 0.5f), scale = new Vector3(thick, 0.16f, side) });
        parts.Add(new SpeciesAsset.Part { mesh = cube, material = plank, offset = new Vector3(side + thick * 0.5f, 0.08f, side * 0.5f), scale = new Vector3(thick, 0.16f, side) });

        // Piles of 27, 18, 9 and 14 small cubes, a third of a cell each.
        (int cellX, int cellZ, int count, Material material)[] piles =
        {
            (2, 2, 27, stone), (1, 2, 18, stone), (2, 1, 14, dirt), (0, 2, 9, dirt), (1, 1, 5, dirt)
        };
        foreach ((int cellX, int cellZ, int count, Material material) in piles)
        {
            for (int i = 0; i < count; i++)
            {
                int spot = i % 9;
                var centre = new Vector3(cellX + (spot % 3 + 0.5f) / 3f, (i / 9 + 0.5f) / 3f, cellZ + (spot / 3 + 0.5f) / 3f);
                parts.Add(new SpeciesAsset.Part { mesh = cube, material = material, offset = centre, scale = Vector3.one * 0.3f });
            }
        }

        return parts;
    }

    private static Material Tinted(Color colour)
    {
        var material = new Material(AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PlantTrunk.mat"));
        material.SetTexture("_BaseMap", null);
        material.SetColor("_BaseColor", colour);
        return material;
    }

    // A sheaf: stalks standing together, tied in the middle.
    private static IReadOnlyList<SpeciesAsset.Part> SheafParts()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PlantStraw.mat");
        Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        var parts = new List<SpeciesAsset.Part>();
        for (int i = 0; i < 9; i++)
        {
            float x = (i % 3 - 1) * 0.12f;
            float z = (i / 3 - 1) * 0.12f;
            parts.Add(new SpeciesAsset.Part { mesh = cube, material = material, offset = new Vector3(x, 0.6f, z), scale = new Vector3(0.08f, 1.2f, 0.08f) });
        }

        var tie = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/PlantTrunk.mat");
        parts.Add(new SpeciesAsset.Part { mesh = cube, material = tie, offset = new Vector3(0f, 0.6f, 0f), scale = new Vector3(0.42f, 0.1f, 0.42f) });
        return parts;
    }

    private static Mesh RotatedOnItsSide(Mesh mesh)
    {
        var copy = UnityEngine.Object.Instantiate(mesh);
        var vertices = copy.vertices;
        var normals = copy.normals;
        Quaternion turn = Quaternion.Euler(0f, 0f, 90f) * Quaternion.Euler(0f, 30f, 0f);
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = turn * vertices[i];
            if (i < normals.Length) normals[i] = turn * normals[i];
        }

        copy.vertices = vertices;
        copy.normals = normals;
        copy.RecalculateBounds();
        return copy;
    }

    // Renders parts in a preview scene with an orthographic camera looking down at pitch degrees,
    // framed on their bounds (margin above 1 leaves room around them). The transparency comes from
    // rendering on black and on white: the difference is what lets the background through.
    private sealed class PreviewRenderer : IDisposable
    {
        private readonly Scene scene;
        private readonly Camera camera;
        private readonly RenderTexture target;
        private readonly GameObject root;

        public PreviewRenderer()
        {
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Item");
            SceneManager.MoveGameObjectToScene(root, scene);

            var lightObject = new GameObject("Sun");
            SceneManager.MoveGameObjectToScene(lightObject, scene);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -40f, 0f);

            var fillObject = new GameObject("Fill");
            SceneManager.MoveGameObjectToScene(fillObject, scene);
            var fill = fillObject.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fillObject.transform.rotation = Quaternion.Euler(20f, 150f, 0f);

            var cameraObject = new GameObject("Camera");
            SceneManager.MoveGameObjectToScene(cameraObject, scene);
            camera = cameraObject.AddComponent<Camera>();
            camera.scene = scene;
            camera.cameraType = CameraType.Preview;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            target = new RenderTexture(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
        }

        public Texture2D Render(IReadOnlyList<SpeciesAsset.Part> parts, float pitch, float margin)
        {
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
            }

            var bounds = new Bounds();
            bool first = true;
            foreach (SpeciesAsset.Part part in parts)
            {
                if (part.mesh == null || part.material == null) continue;

                var partObject = new GameObject("Part");
                partObject.transform.SetParent(root.transform, false);
                partObject.transform.localPosition = part.offset;
                partObject.transform.localScale = part.scale;
                partObject.AddComponent<MeshFilter>().sharedMesh = part.mesh;
                var meshRenderer = partObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = part.material;
                if (first) bounds = meshRenderer.bounds;
                else bounds.Encapsulate(meshRenderer.bounds);
                first = false;
            }

            Quaternion view = Quaternion.Euler(pitch, -35f, 0f);
            float radius = Mathf.Max(0.01f, bounds.extents.magnitude);
            camera.transform.rotation = view;
            camera.transform.position = bounds.center - view * Vector3.forward * (radius * 4f);
            camera.orthographicSize = radius * margin;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = radius * 10f;

            Color[] onBlack = Shoot(Color.black);
            Color[] onWhite = Shoot(Color.white);
            var pixels = new Color[onBlack.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color b = onBlack[i];
                Color w = onWhite[i];
                float alpha = Mathf.Clamp01(1f - ((w.r - b.r) + (w.g - b.g) + (w.b - b.b)) / 3f);
                pixels[i] = alpha > 0.002f ? new Color(b.r / alpha, b.g / alpha, b.b / alpha, alpha) : Color.clear;
            }

            var icon = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }

        private Color[] Shoot(Color background)
        {
            camera.backgroundColor = background;
            camera.Render();
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var shot = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
            shot.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
            shot.Apply();
            RenderTexture.active = previous;
            Color[] pixels = shot.GetPixels();
            UnityEngine.Object.DestroyImmediate(shot);
            return pixels;
        }

        public void Dispose()
        {
            camera.targetTexture = null;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
