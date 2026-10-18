using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Voxel.EditorTools
{
    // Builds animals' models from sculpted meshes (FBX files in Assets/Models) instead of boxes. Each
    // mesh is scaled and placed in the animal's space (facing +z, feet at y = 0, the middle of its legs
    // on the origin), posed if needed (the deer's head, turned aside in its model, is turned forward),
    // simplified (edge collapses chosen by quadric error, so flat areas lose their triangles first and
    // the silhouette keeps its shape), cut along planes into the parts the animation moves (legs,
    // ears, tail, each about a pivot on its cut, the cuts capped) and coloured: by region rules for a
    // model without texture (the fox), or from the model's colour texture, its colours gathered into
    // a few tones (the deer). Each part is one mesh per colour, drawn flat-shaded with one material,
    // written into the species asset like AnimalModelBuilder's boxes (meshes in Assets/Species/Models,
    // <Species>_<Part>_<Colour>.asset, each with a smooth-normal shadow mesh).
    //
    // The regions are boxes in the animal's space, found by looking at the model's profile; a new
    // model needs its own. Run Eco > Rebuild animals from models after changing anything below.
    public static class ModelAnimalBuilder
    {
        private const string ModelFolder = "Assets/Species/Models";
        private const string MaterialFolder = "Assets/Materials";

        // A part the animation moves. A pivot left at zero is worked out: the middle of the part's cut.
        private struct Region
        {
            public string Name;
            public AnimalPart Motion;
            public Vector3 Pivot;
        }

        // The motions of AnimalSpeciesAsset.PartMotion, by name, so this file reads on its own.
        private enum AnimalPart { Body, Ear, FrontLeg, HindLeg, Tail }

        // An eye drawn open over the model (the left one mirrors this right one).
        private struct Eye
        {
            public Vector3 InnerCorner;
            public Vector3 OuterCorner;
            public Vector3 Facing;
            public float Length;
            public float Height;
        }

        // One animal made from a model.
        private sealed class Spec
        {
            public string Species;
            public string Model;
            public float Length;

            // Where the middle of the legs lies in the scaled model: x from the centre of its bounds,
            // z from its back end. That point becomes the origin.
            public Vector2 LegsCentre;
            public int Triangles;

            // The finished model, its pivots and eyes are scaled by this. Everything else here
            // (Length, LegsCentre, the pose, cuts and regions) stays in the size it was measured in.
            public float Scale = 1f;

            // Optional, in the animal's space, before simplifying.
            public Func<Vector3, Vector3> Pose;
            public Vector4[] Cuts;
            public Region[] Regions;
            public Func<Vector3, int> RegionOf;

            // Colours by rule: a tone index for a triangle (its region, centre and normal), and the
            // name and material of each tone.
            public Func<int, Vector3, Vector3, int> ColourOf;
            public string[] ToneNames;
            public string[] ToneMaterials;

            // Or colours from the model's texture, gathered into this many tones, whose materials
            // (Animal<Species>_Tone<n>) are written by the build.
            public string Texture;
            public int Tones;

            public Eye? Eyes;
        }

        // ---- The fox (no texture: painted by region) ----

        private enum FoxTone { Fur, White, Dark }

        private static readonly Region[] FoxRegions =
        {
            new Region { Name = "Body", Motion = AnimalPart.Body },
            new Region { Name = "FrontLegLeft", Motion = AnimalPart.FrontLeg, Pivot = new Vector3(-0.09f, 0.29f, 0.22f) },
            new Region { Name = "FrontLegRight", Motion = AnimalPart.FrontLeg, Pivot = new Vector3(0.09f, 0.29f, 0.22f) },
            new Region { Name = "HindLegLeft", Motion = AnimalPart.HindLeg, Pivot = new Vector3(-0.08f, 0.29f, -0.22f) },
            new Region { Name = "HindLegRight", Motion = AnimalPart.HindLeg, Pivot = new Vector3(0.08f, 0.29f, -0.22f) },
            new Region { Name = "EarLeft", Motion = AnimalPart.Ear, Pivot = new Vector3(-0.07f, 0.65f, 0.38f) },
            new Region { Name = "EarRight", Motion = AnimalPart.Ear, Pivot = new Vector3(0.07f, 0.65f, 0.38f) },
            new Region { Name = "Tail", Motion = AnimalPart.Tail, Pivot = new Vector3(0f, 0.42f, -0.32f) }
        };

        // The region of a triangle, by its centre: an index in FoxRegions.
        private static int FoxRegion(Vector3 c)
        {
            int side = c.x < 0f ? 0 : 1;
            if (c.z < -0.32f) return 7;
            if (c.z > -0.34f && c.z < -0.10f && c.y < 0.29f) return 3 + side;
            if (c.z > 0.10f && c.z < 0.34f && c.y < 0.29f) return 1 + side;
            if (c.z > 0.28f && c.y > 0.665f && Mathf.Abs(c.x) > 0.02f) return 5 + side;
            return 0;
        }

        // A red fox's coat: white throat, chest, belly, lower jaw and tail tip, black socks, ear backs
        // and nose.
        private static int FoxColour(int region, Vector3 c, Vector3 normal)
        {
            switch (FoxRegions[region].Motion)
            {
                case AnimalPart.Tail:
                    return (int)(c.z < -0.58f ? FoxTone.White : FoxTone.Fur);
                case AnimalPart.FrontLeg:
                case AnimalPart.HindLeg:
                    return (int)(c.y < 0.17f ? FoxTone.Dark : FoxTone.Fur);
                case AnimalPart.Ear:
                    // Only the ear itself, above the skull: the head around its base stays fur.
                    if (c.y > 0.725f || (c.y > 0.69f && normal.z < -0.2f)) return (int)FoxTone.Dark;
                    return (int)(c.y > 0.68f && normal.z > 0.5f ? FoxTone.White : FoxTone.Fur);
                default:
                    if (c.z > 0.575f) return (int)FoxTone.Dark;
                    if (c.z > 0.36f && c.y < 0.53f) return (int)FoxTone.White;

                    // The bib, down the middle of the throat and chest, never on the shoulders.
                    if (c.z > 0.12f && c.z <= 0.42f && Mathf.Abs(c.x) < 0.075f && c.y < 0.56f && normal.y < 0.3f
                        && (normal.z > 0.2f || normal.y < -0.3f)) return (int)FoxTone.White;
                    if (c.z > -0.22f && c.z < 0.15f && Mathf.Abs(c.x) < 0.07f && normal.y < -0.6f) return (int)FoxTone.White;
                    return (int)FoxTone.Fur;
            }
        }

        private static readonly Spec Fox = new Spec
        {
            Species = "Fox",
            Model = "Assets/Models/Fox/Fox.fbx",
            Length = 1.3f,
            LegsCentre = new Vector2(0f, 0.70f),
            Triangles = 2400,

            // Planes the mesh is cut along before its triangles are sorted into regions, so that a
            // moving part meets the body on one clean flat cut through its pivot (a hinge) instead of
            // along the ragged edges of whole triangles: behind the rump for the tail, under the belly
            // for the legs, above the skull for the ears. Each plane is a normal and its distance from
            // the origin.
            Cuts = new[] { new Vector4(0f, 0f, 1f, -0.32f), new Vector4(0f, 1f, 0f, 0.29f), new Vector4(0f, 1f, 0f, 0.665f) },
            Regions = FoxRegions,
            RegionOf = FoxRegion,
            ColourOf = FoxColour,
            ToneNames = new[] { "Fur", "White", "Dark" },
            ToneMaterials = new[] { "AnimalFox", "AnimalFox_White", "AnimalFox_Dark" },

            // The model only has a closed slit there, from its inner corner by the nose to its outer
            // corner, rising towards the ear. The eye is drawn open over it, as a dark almond with a glint.
            Eyes = new Eye
            {
                InnerCorner = new Vector3(0.034f, 0.580f, 0.470f),
                OuterCorner = new Vector3(0.070f, 0.592f, 0.448f),
                Facing = new Vector3(0.55f, 0.1f, 0.83f),
                Length = 0.042f,
                Height = 0.018f
            }
        };

        // ---- The deer (coloured from its texture) ----

        // The model holds its head turned about 38° to its right. Turned forward about the top of the
        // neck, fully from the head up and fading out down the neck and back over the shoulders, so the
        // deer looks where it walks.
        private static Vector3 DeerPose(Vector3 p)
        {
            var neck = new Vector3(0.117f, 0f, 0.453f);
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.95f, 1.12f, p.y))
                           * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.10f, 0.30f, p.z));
            if (weight <= 0f) return p;

            Vector3 offset = p - neck;
            return neck + Quaternion.Euler(0f, -38f * weight, 0f) * offset;
        }

        // The legs below the belly, the hind ones slanting back. Ears and tail are too small to move.
        private static int DeerRegion(Vector3 c)
        {
            int side = c.x < 0f ? 0 : 1;
            if (c.y < 0.52f && c.z > -0.70f && c.z < -0.18f) return 3 + side;
            if (c.y < 0.52f && c.z > 0.24f && c.z < 0.70f) return 1 + side;
            return 0;
        }

        private static readonly Spec Deer = new Spec
        {
            Species = "Deer",
            Model = "Assets/Models/Deer/Deer.fbx",
            Length = 1.45f,
            LegsCentre = new Vector2(-0.108f, 0.592f),
            Triangles = 3600,

            // 1.9 blocks long, about 1.25 at the shoulder and 2.4 to the tips of the antlers: a red deer.
            Scale = 1.9f / 1.45f,
            Pose = DeerPose,
            Cuts = new[] { new Vector4(0f, 1f, 0f, 0.52f) },
            Regions = new[]
            {
                new Region { Name = "Body", Motion = AnimalPart.Body },
                new Region { Name = "FrontLegLeft", Motion = AnimalPart.FrontLeg },
                new Region { Name = "FrontLegRight", Motion = AnimalPart.FrontLeg },
                new Region { Name = "HindLegLeft", Motion = AnimalPart.HindLeg },
                new Region { Name = "HindLegRight", Motion = AnimalPart.HindLeg }
            },
            RegionOf = DeerRegion,
            Texture = "Assets/Models/Deer/Deer_Diffuse.png",
            Tones = 6
        };

        [MenuItem("Eco/Rebuild animals from models")]
        public static void BuildAll()
        {
            Build(Fox);
            Build(Deer);
            AssetDatabase.SaveAssets();
        }

        private static void Build(Spec spec)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(spec.Model);
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>($"Assets/Species/{spec.Species}.asset");
            if (model == null || asset == null)
            {
                Debug.LogError($"Model {spec.Model} or the species asset of {spec.Species} is missing.");
                return;
            }

            // Every mesh of the model, in the model's space, then scaled to the length wanted, its feet
            // at y = 0 and the middle of its legs on the origin, then posed.
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = filter.sharedMesh;
                Matrix4x4 toModel = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                toModel = Matrix4x4.TRS(Vector3.zero, model.transform.localRotation, model.transform.localScale) * toModel;
                int first = positions.Count;
                foreach (Vector3 vertex in mesh.vertices) positions.Add(toModel.MultiplyPoint3x4(vertex));
                Vector2[] meshUvs = mesh.uv;
                for (int i = 0; i < mesh.vertexCount; i++) uvs.Add(i < meshUvs.Length ? meshUvs[i] : Vector2.zero);
                foreach (int index in mesh.triangles) indices.Add(first + index);
            }

            var bounds = new Bounds(positions[0], Vector3.zero);
            foreach (Vector3 position in positions) bounds.Encapsulate(position);
            float scale = spec.Length / bounds.size.z;
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 p = positions[i];
                p = new Vector3((p.x - bounds.center.x) * scale - spec.LegsCentre.x, (p.y - bounds.min.y) * scale,
                    (p.z - bounds.min.z) * scale - spec.LegsCentre.y);
                positions[i] = spec.Pose != null ? spec.Pose(p) : p;
            }

            // The texture's colour on the model, before the triangles that carry its coordinates go.
            ColourField field = spec.Texture != null ? new ColourField(spec.Texture, positions, uvs, indices) : null;

            var simplifier = new Simplifier(positions, indices);
            int before = simplifier.TriangleCount;
            simplifier.Reduce(spec.Triangles);
            simplifier.Result(out List<Vector3> vertices, out List<int> faces);

            // Eyes drawn over the model are laid on the surface before the caps below are added inside it.
            Mesh almonds = null, glints = null;
            if (spec.Eyes.HasValue) BuildEyes(vertices, faces, spec.Eyes.Value, out almonds, out glints);

            foreach (Vector4 cut in spec.Cuts) SplitByPlane(vertices, faces, new Vector3(cut.x, cut.y, cut.z), cut.w);

            // Each triangle's part and colour, the colours then smoothed, then one flat-shaded mesh per pair.
            int faceCount = faces.Count / 3;
            var regionsOf = new int[faceCount];
            var tones = new int[faceCount];
            var sampled = new Color[faceCount];
            var areas = new float[faceCount];
            for (int i = 0; i < faceCount; i++)
            {
                int f = i * 3;
                Vector3 a = vertices[faces[f]], b = vertices[faces[f + 1]], c = vertices[faces[f + 2]];
                regionsOf[i] = -1;
                if (!TryFacetNormal(a, b, c, out Vector3 normal)) continue;

                Vector3 centre = (a + b + c) / 3f;
                regionsOf[i] = spec.RegionOf(centre);
                if (field == null)
                {
                    tones[i] = spec.ColourOf(regionsOf[i], centre, normal);
                    continue;
                }

                sampled[i] = (field.At(centre) * 2f + field.At((a + centre) * 0.5f) + field.At((b + centre) * 0.5f) + field.At((c + centre) * 0.5f)) / 5f;
                areas[i] = Vector3.Cross(b - a, c - a).magnitude;
            }

            string[] toneNames;
            Material[] materials;
            if (field == null)
            {
                toneNames = spec.ToneNames;
                materials = Array.ConvertAll(spec.ToneMaterials, name => AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{name}.mat"));
            }
            else
            {
                Color[] palette = GatherTones(sampled, areas, regionsOf, spec.Tones, tones);
                toneNames = new string[palette.Length];
                materials = new Material[palette.Length];
                for (int t = 0; t < palette.Length; t++)
                {
                    toneNames[t] = $"Tone{t}";
                    materials[t] = SaveToneMaterial($"Animal{spec.Species}_Tone{t}", palette[t]);
                }
            }

            SmoothColours(faces, regionsOf, tones, toneNames.Length);

            // The surface's smooth normals, for the shadow meshes, worked out before the caps.
            int surfaceFaceCount = faceCount;
            Vector3[] smoothNormals = SmoothNormals(vertices, faces, surfaceFaceCount);

            Vector3[] pivots = Pivots(spec.Regions, vertices, faces, regionsOf);
            AddCaps(vertices, faces, ref regionsOf, ref tones);
            faceCount = regionsOf.Length;

            if (spec.Scale != 1f)
            {
                for (int i = 0; i < vertices.Count; i++) vertices[i] *= spec.Scale;
                for (int r = 0; r < pivots.Length; r++) pivots[r] *= spec.Scale;
                foreach (Mesh eyes in new[] { almonds, glints })
                {
                    if (eyes == null) continue;

                    Vector3[] corners = eyes.vertices;
                    for (int i = 0; i < corners.Length; i++) corners[i] *= spec.Scale;
                    eyes.vertices = corners;
                    eyes.RecalculateBounds();
                }
            }

            var groups = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < faceCount; i++)
            {
                if (regionsOf[i] < 0) continue;

                int key = regionsOf[i] * 64 + tones[i];
                if (!groups.TryGetValue(key, out List<int> list)) groups[key] = list = new List<int>();
                list.Add(i * 3);
            }

            var written = new HashSet<string>();
            var serialized = new SerializedObject(asset);
            SerializedProperty parts = serialized.FindProperty("parts");
            SerializedProperty rig = serialized.FindProperty("rig");
            int eyeParts = spec.Eyes.HasValue ? 2 : 0;
            parts.arraySize = groups.Count + eyeParts;
            rig.arraySize = groups.Count + eyeParts;
            int partIndex = 0;

            void WritePart(Mesh mesh, Mesh shadow, Material material, AnimalPart motion, Vector3 pivot)
            {
                SerializedProperty part = parts.GetArrayElementAtIndex(partIndex);
                part.FindPropertyRelative("mesh").objectReferenceValue = mesh;
                part.FindPropertyRelative("shadowMesh").objectReferenceValue = shadow;
                part.FindPropertyRelative("material").objectReferenceValue = material;
                part.FindPropertyRelative("offset").vector3Value = Vector3.zero;
                part.FindPropertyRelative("scale").vector3Value = Vector3.one;
                SerializedProperty entry = rig.GetArrayElementAtIndex(partIndex);
                entry.FindPropertyRelative("motion").enumValueIndex = (int)motion;
                entry.FindPropertyRelative("pivot").vector3Value = motion == AnimalPart.Body ? Vector3.zero : pivot;
                partIndex++;
            }

            if (spec.Eyes.HasValue)
            {
                // Both eyes, each a dark almond and a glint, moving with the body.
                foreach ((string colourName, int tone, Mesh mesh) in new[] { ("Dark", (int)FoxTone.Dark, almonds), ("White", (int)FoxTone.White, glints) })
                {
                    string name = $"{spec.Species}_Eyes_{colourName}";
                    written.Add(name);
                    WritePart(SaveMesh($"{ModelFolder}/{name}.asset", name, mesh), null, materials[tone], AnimalPart.Body, Vector3.zero);
                }
            }

            foreach (KeyValuePair<int, List<int>> group in groups)
            {
                int region = group.Key / 64;
                int tone = group.Key % 64;
                string name = $"{spec.Species}_{spec.Regions[region].Name}_{toneNames[tone]}";
                Mesh mesh = SaveMesh($"{ModelFolder}/{name}.asset", name, FlatMesh(vertices, faces, group.Value));
                Mesh shadow = SaveMesh($"{ModelFolder}/{name}_Shadow.asset", name + "_Shadow",
                    SmoothMesh(vertices, faces, group.Value, smoothNormals, surfaceFaceCount));
                written.Add(name);
                written.Add(name + "_Shadow");
                WritePart(mesh, shadow, materials[tone], spec.Regions[region].Motion, pivots[region]);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);

            // Meshes of parts this build no longer makes.
            foreach (string guid in AssetDatabase.FindAssets($"{spec.Species}_ t:Mesh", new[] { ModelFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = Path.GetFileNameWithoutExtension(path);
                if (file.StartsWith(spec.Species + "_") && !written.Contains(file) && AssetDatabase.LoadMainAssetAtPath(path) is Mesh)
                {
                    AssetDatabase.DeleteAsset(path);
                }
            }

            Debug.Log($"{spec.Species}: {before} triangles simplified to {surfaceFaceCount}, in {groups.Count} parts of {toneNames.Length} colours.");
        }

        // Each region's pivot: its own, or the middle of the vertices it shares with other regions (its cut).
        private static Vector3[] Pivots(Region[] regions, List<Vector3> vertices, List<int> faces, int[] regionsOf)
        {
            var regionsAt = new Dictionary<int, HashSet<int>>();
            for (int i = 0; i < regionsOf.Length; i++)
            {
                if (regionsOf[i] < 0) continue;

                for (int k = 0; k < 3; k++)
                {
                    int vertex = faces[i * 3 + k];
                    if (!regionsAt.TryGetValue(vertex, out HashSet<int> set)) regionsAt[vertex] = set = new HashSet<int>();
                    set.Add(regionsOf[i]);
                }
            }

            var pivots = new Vector3[regions.Length];
            for (int r = 0; r < regions.Length; r++)
            {
                pivots[r] = regions[r].Pivot;
                if (regions[r].Pivot != Vector3.zero || regions[r].Motion == AnimalPart.Body) continue;

                Vector3 sum = Vector3.zero;
                int count = 0;
                foreach (KeyValuePair<int, HashSet<int>> vertex in regionsAt)
                {
                    if (vertex.Value.Count < 2 || !vertex.Value.Contains(r)) continue;

                    sum += vertices[vertex.Key];
                    count++;
                }

                if (count > 0) pivots[r] = sum / count;
            }

            return pivots;
        }

        // The colours of a model's texture over its surface: each original triangle's colour (the
        // texture sampled at a few points of it), found again by position, from the nearest triangle
        // centre, once the triangles and their texture coordinates are gone.
        private sealed class ColourField
        {
            private const float CellSize = 0.03f;
            private readonly Dictionary<(int, int, int), List<int>> cells = new Dictionary<(int, int, int), List<int>>();
            private readonly List<Vector3> centres = new List<Vector3>();
            private readonly List<Color> colours = new List<Color>();

            public ColourField(string texturePath, List<Vector3> positions, List<Vector2> uvs, List<int> indices)
            {
                var texture = new Texture2D(2, 2);
                texture.LoadImage(File.ReadAllBytes(texturePath));
                for (int f = 0; f < indices.Count; f += 3)
                {
                    int a = indices[f], b = indices[f + 1], c = indices[f + 2];
                    Vector2 uvCentre = (uvs[a] + uvs[b] + uvs[c]) / 3f;
                    Color colour = texture.GetPixelBilinear(uvCentre.x, uvCentre.y) * 2f;
                    foreach (int corner in new[] { a, b, c })
                    {
                        Vector2 uv = Vector2.Lerp(uvCentre, uvs[corner], 0.5f);
                        colour += texture.GetPixelBilinear(uv.x, uv.y);
                    }

                    Vector3 centre = (positions[a] + positions[b] + positions[c]) / 3f;
                    (int, int, int) cell = CellOf(centre);
                    if (!cells.TryGetValue(cell, out List<int> list)) cells[cell] = list = new List<int>();
                    list.Add(centres.Count);
                    centres.Add(centre);
                    colours.Add(colour / 5f);
                }

                UnityEngine.Object.DestroyImmediate(texture);
            }

            public Color At(Vector3 point)
            {
                (int x, int y, int z) = CellOf(point);
                for (int reach = 1; reach <= 4; reach++)
                {
                    int nearest = -1;
                    float best = float.MaxValue;
                    for (int dx = -reach; dx <= reach; dx++)
                    for (int dy = -reach; dy <= reach; dy++)
                    for (int dz = -reach; dz <= reach; dz++)
                    {
                        if (!cells.TryGetValue((x + dx, y + dy, z + dz), out List<int> list)) continue;

                        foreach (int i in list)
                        {
                            float distance = (centres[i] - point).sqrMagnitude;
                            if (distance < best)
                            {
                                best = distance;
                                nearest = i;
                            }
                        }
                    }

                    if (nearest >= 0) return colours[nearest];
                }

                return Color.grey;
            }

            private static (int, int, int) CellOf(Vector3 p) =>
                (Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize), Mathf.FloorToInt(p.z / CellSize));
        }

        // Gathers the triangles' colours into a few tones (k-means, weighted by area, started from
        // colours spread by brightness, so the result is the same every time), darkest first; each
        // triangle gets the nearest tone.
        private static Color[] GatherTones(Color[] colours, float[] areas, int[] regions, int count, int[] tones)
        {
            var chosen = new List<int>();
            for (int i = 0; i < colours.Length; i++)
            {
                if (regions[i] >= 0) chosen.Add(i);
            }

            chosen.Sort((p, q) => Brightness(colours[p]).CompareTo(Brightness(colours[q])));
            float total = 0f;
            foreach (int i in chosen) total += areas[i];

            var centres = new Vector3[count];
            float running = 0f;
            int next = 0;
            foreach (int i in chosen)
            {
                running += areas[i];
                while (next < count && running >= total * (next + 0.5f) / count)
                {
                    centres[next++] = ToVector(colours[i]);
                }
            }

            for (int iteration = 0; iteration < 30; iteration++)
            {
                var sums = new Vector3[count];
                var weights = new float[count];
                foreach (int i in chosen)
                {
                    int tone = Nearest(centres, ToVector(colours[i]));
                    sums[tone] += ToVector(colours[i]) * areas[i];
                    weights[tone] += areas[i];
                }

                for (int t = 0; t < count; t++)
                {
                    if (weights[t] > 0f) centres[t] = sums[t] / weights[t];
                }
            }

            Array.Sort(centres, (p, q) => (p.x + p.y + p.z).CompareTo(q.x + q.y + q.z));
            foreach (int i in chosen) tones[i] = Nearest(centres, ToVector(colours[i]));
            return Array.ConvertAll(centres, c => new Color(c.x, c.y, c.z));
        }

        private static float Brightness(Color c) => c.r + c.g + c.b;

        private static Vector3 ToVector(Color c) => new Vector3(c.r, c.g, c.b);

        private static int Nearest(Vector3[] centres, Vector3 colour)
        {
            int nearest = 0;
            for (int t = 1; t < centres.Length; t++)
            {
                if ((centres[t] - colour).sqrMagnitude < (centres[nearest] - colour).sqrMagnitude) nearest = t;
            }

            return nearest;
        }

        // A tone's material: a copy of the fox's fur (URP Lit, instancing on), in the tone's colour.
        private static Material SaveToneMaterial(string name, Color colour)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var template = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/AnimalFox.mat");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(template) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.CopyPropertiesFromMaterial(template);
            }

            material.enableInstancing = true;
            material.SetColor("_BaseColor", colour);
            material.color = colour;
            EditorUtility.SetDirty(material);
            return material;
        }

        // Rules by position and facing, or a texture's colours, leave stray triangles and ragged
        // edges between colours: a triangle whose neighbours across at least two of its sides share
        // another colour takes it. Only neighbours in the same part count, so a sock never spreads up
        // into the body.
        private static void SmoothColours(List<int> faces, int[] regions, int[] tones, int toneCount)
        {
            int faceCount = regions.Length;
            var trianglesOfEdge = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < faceCount; i++)
            {
                if (regions[i] < 0) continue;

                for (int k = 0; k < 3; k++)
                {
                    int u = faces[i * 3 + k], v = faces[i * 3 + (k + 1) % 3];
                    (int, int) edge = u < v ? (u, v) : (v, u);
                    if (!trianglesOfEdge.TryGetValue(edge, out List<int> list)) trianglesOfEdge[edge] = list = new List<int>();
                    list.Add(i);
                }
            }

            for (int pass = 0; pass < 3; pass++)
            {
                var next = (int[])tones.Clone();
                for (int i = 0; i < faceCount; i++)
                {
                    if (regions[i] < 0) continue;

                    var votes = new int[toneCount];
                    for (int k = 0; k < 3; k++)
                    {
                        int u = faces[i * 3 + k], v = faces[i * 3 + (k + 1) % 3];
                        foreach (int j in trianglesOfEdge[u < v ? (u, v) : (v, u)])
                        {
                            if (j != i && regions[j] == regions[i]) votes[tones[j]]++;
                        }
                    }

                    for (int t = 0; t < toneCount; t++)
                    {
                        if (t != tones[i] && votes[t] >= 2) next[i] = t;
                    }
                }

                Array.Copy(next, tones, faceCount);
            }
        }

        // Cuts every triangle the plane n·p = offset crosses into the pieces on either side, so that
        // the plane becomes an edge of the mesh. The points made on an edge are shared by the two
        // triangles along it, so the mesh stays closed.
        private static void SplitByPlane(List<Vector3> vertices, List<int> faces, Vector3 normal, float offset)
        {
            var distances = new List<float>();
            foreach (Vector3 vertex in vertices)
            {
                float distance = Vector3.Dot(normal, vertex) - offset;
                distances.Add(Mathf.Abs(distance) < 1e-6f ? 0f : distance);
            }

            var madeOnEdge = new Dictionary<(int, int), int>();
            int PointOn(int a, int b)
            {
                (int, int) edge = a < b ? (a, b) : (b, a);
                if (madeOnEdge.TryGetValue(edge, out int made)) return made;

                float t = distances[edge.Item1] / (distances[edge.Item1] - distances[edge.Item2]);
                made = vertices.Count;
                vertices.Add(Vector3.Lerp(vertices[edge.Item1], vertices[edge.Item2], t));
                distances.Add(0f);
                madeOnEdge[edge] = made;
                return made;
            }

            var result = new List<int>();
            var above = new List<int>(4);
            var below = new List<int>(4);
            for (int f = 0; f < faces.Count; f += 3)
            {
                int a = faces[f], b = faces[f + 1], c = faces[f + 2];
                bool anyAbove = distances[a] > 0f || distances[b] > 0f || distances[c] > 0f;
                bool anyBelow = distances[a] < 0f || distances[b] < 0f || distances[c] < 0f;
                if (!anyAbove || !anyBelow)
                {
                    result.Add(a);
                    result.Add(b);
                    result.Add(c);
                    continue;
                }

                // Going round the triangle: each corner joins its side (both when it lies on the
                // plane), and each side that crosses the plane adds its crossing point to both.
                above.Clear();
                below.Clear();
                for (int k = 0; k < 3; k++)
                {
                    int i = faces[f + k], j = faces[f + (k + 1) % 3];
                    if (distances[i] >= 0f) above.Add(i);
                    if (distances[i] <= 0f) below.Add(i);
                    if (distances[i] * distances[j] < 0f)
                    {
                        int point = PointOn(i, j);
                        above.Add(point);
                        below.Add(point);
                    }
                }

                foreach (List<int> piece in new[] { above, below })
                {
                    for (int k = 1; k + 1 < piece.Count; k++)
                    {
                        result.Add(piece[0]);
                        result.Add(piece[k]);
                        result.Add(piece[k + 1]);
                    }
                }
            }

            faces.Clear();
            faces.AddRange(result);
        }

        // Closes the cuts between parts. The mesh is hollow and its inner faces are not drawn, so a
        // crack opening at a joint when a leg or the tail swings would show through the animal. Both
        // sides of every cut get a cap, a fan from the cut's edge to its middle, each triangle
        // coloured like the one along its edge: at rest the caps lie inside, back to back; in motion
        // the crack shows them, opaque.
        private static void AddCaps(List<Vector3> vertices, List<int> faces, ref int[] regions, ref int[] colours)
        {
            var faceOfEdge = new Dictionary<(int, int), int>();
            for (int i = 0; i < regions.Length; i++)
            {
                if (regions[i] < 0) continue;

                for (int k = 0; k < 3; k++) faceOfEdge[(faces[i * 3 + k], faces[i * 3 + (k + 1) % 3])] = i;
            }

            // An edge of the cut, as its face goes round it, with the face on the other side in another part.
            var cut = new List<(int U, int V, int Face)>();
            foreach (KeyValuePair<(int, int), int> edge in faceOfEdge)
            {
                if (faceOfEdge.TryGetValue((edge.Key.Item2, edge.Key.Item1), out int other) && regions[other] != regions[edge.Value])
                {
                    cut.Add((edge.Key.Item1, edge.Key.Item2, edge.Value));
                }
            }

            // Each cut's middle: the mean of the vertices of its connected edges.
            var parent = new Dictionary<int, int>();
            int Find(int v)
            {
                if (!parent.TryGetValue(v, out int p)) return v;
                int root = Find(p);
                parent[v] = root;
                return root;
            }

            foreach ((int u, int v, int _) in cut)
            {
                int ru = Find(u), rv = Find(v);
                if (ru != rv) parent[ru] = rv;
            }

            var sums = new Dictionary<int, Vector3>();
            var counts = new Dictionary<int, int>();
            var counted = new HashSet<int>();
            foreach ((int u, int v, int _) in cut)
            {
                foreach (int w in new[] { u, v })
                {
                    if (!counted.Add(w)) continue;

                    int root = Find(w);
                    sums.TryGetValue(root, out Vector3 sum);
                    counts.TryGetValue(root, out int count);
                    sums[root] = sum + vertices[w];
                    counts[root] = count + 1;
                }
            }

            var middles = new Dictionary<int, int>();
            foreach (KeyValuePair<int, Vector3> sum in sums)
            {
                middles[sum.Key] = vertices.Count;
                vertices.Add(sum.Value / counts[sum.Key]);
            }

            // Going round the edge the other way from its face keeps the cap's facing consistent with it.
            var allRegions = new List<int>(regions);
            var allColours = new List<int>(colours);
            foreach ((int u, int v, int face) in cut)
            {
                faces.Add(v);
                faces.Add(u);
                faces.Add(middles[Find(u)]);
                allRegions.Add(regions[face]);
                allColours.Add(colours[face]);
            }

            regions = allRegions.ToArray();
            colours = allColours.ToArray();
        }

        // Each eye is an almond, a ring of points round a slightly raised middle, laid on the
        // simplified surface: every point is projected onto it and lifted a little off it, so the eye
        // follows the facets. A small glint sits in its upper half, towards the nose.
        private static void BuildEyes(List<Vector3> vertices, List<int> faces, Eye eye, out Mesh almonds, out Mesh glints)
        {
            const int ring = 12;
            var almondCorners = new List<Vector3>();
            var almondTriangles = new List<int>();
            var glintCorners = new List<Vector3>();
            var glintTriangles = new List<int>();
            foreach (float side in new[] { 1f, -1f })
            {
                var mirror = new Vector3(side, 1f, 1f);
                Vector3 inner = Vector3.Scale(eye.InnerCorner, mirror);
                Vector3 outer = Vector3.Scale(eye.OuterCorner, mirror);
                Vector3 centre = (inner + outer) * 0.5f;
                Vector3 normal = SurfaceNormal(vertices, faces, centre, Vector3.Scale(eye.Facing, mirror).normalized, eye.Length);
                Vector3 along = Vector3.ProjectOnPlane(outer - inner, normal).normalized;
                Vector3 up = Vector3.Cross(normal, along);
                if (up.y < 0f) up = -up;

                int middle = almondCorners.Count;
                almondCorners.Add(OnSurface(vertices, faces, centre, normal, 0.0045f));
                for (int i = 0; i < ring; i++)
                {
                    // An ellipse pulled out towards its ends, which makes them pointed.
                    float angle = i * 2f * Mathf.PI / ring;
                    float cos = Mathf.Cos(angle);
                    float across = Mathf.Sign(cos) * Mathf.Pow(Mathf.Abs(cos), 0.45f);
                    Vector3 point = centre + along * (across * eye.Length * 0.5f) + up * (Mathf.Sin(angle) * eye.Height * 0.5f);
                    almondCorners.Add(OnSurface(vertices, faces, point, normal, 0.002f));
                }

                for (int i = 0; i < ring; i++)
                {
                    AddFacing(almondCorners, almondTriangles, middle, middle + 1 + i, middle + 1 + (i + 1) % ring, normal);
                }

                Vector3 glint = centre - along * (eye.Length * 0.22f) + up * (eye.Height * 0.18f);
                float size = eye.Height * 0.22f;
                int first = glintCorners.Count;
                foreach (Vector3 offset in new[] { along, up, -along, -up })
                {
                    glintCorners.Add(OnSurface(vertices, faces, glint + offset * size, normal, 0.0065f));
                }

                AddFacing(glintCorners, glintTriangles, first, first + 1, first + 2, normal);
                AddFacing(glintCorners, glintTriangles, first, first + 2, first + 3, normal);
            }

            almonds = FlatMesh(almondCorners, almondTriangles, EveryFace(almondTriangles));
            glints = FlatMesh(glintCorners, glintTriangles, EveryFace(glintTriangles));
        }

        // A triangle wound so that it faces the given way.
        private static void AddFacing(List<Vector3> corners, List<int> triangles, int a, int b, int c, Vector3 facing)
        {
            bool turned = Vector3.Dot(Vector3.Cross(corners[b] - corners[a], corners[c] - corners[a]), facing) < 0f;
            triangles.Add(a);
            triangles.Add(turned ? c : b);
            triangles.Add(turned ? b : c);
        }

        private static List<int> EveryFace(List<int> triangles)
        {
            var all = new List<int>();
            for (int f = 0; f < triangles.Count; f += 3) all.Add(f);
            return all;
        }

        // The mean normal of the surface around a point, from the facets turned the given way.
        private static Vector3 SurfaceNormal(List<Vector3> vertices, List<int> faces, Vector3 point, Vector3 facing, float radius)
        {
            Vector3 sum = Vector3.zero;
            for (int f = 0; f < faces.Count; f += 3)
            {
                Vector3 a = vertices[faces[f]], b = vertices[faces[f + 1]], c = vertices[faces[f + 2]];
                if (((a + b + c) / 3f - point).magnitude > radius) continue;

                Vector3 weighted = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(weighted, facing) > 0f) sum += weighted;
            }

            return sum == Vector3.zero ? facing : sum.normalized;
        }

        // The point brought onto the outer surface along the normal, then lifted off it by `lift`.
        private static Vector3 OnSurface(List<Vector3> vertices, List<int> faces, Vector3 point, Vector3 normal, float lift)
        {
            const float reach = 0.04f;
            Vector3 origin = point + normal * reach;
            float nearest = reach * 2f;
            bool hit = false;
            for (int f = 0; f < faces.Count; f += 3)
            {
                if (RayHits(origin, -normal, vertices[faces[f]], vertices[faces[f + 1]], vertices[faces[f + 2]], out float distance) && distance < nearest)
                {
                    nearest = distance;
                    hit = true;
                }
            }

            return (hit ? origin - normal * nearest : point) + normal * lift;
        }

        // Möller–Trumbore: where a ray meets a triangle, either side.
        private static bool RayHits(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0f;
            Vector3 ab = b - a, ac = c - a;
            Vector3 p = Vector3.Cross(direction, ac);
            float det = Vector3.Dot(ab, p);
            if (Mathf.Abs(det) < 1e-9f) return false;

            Vector3 t = origin - a;
            float u = Vector3.Dot(t, p) / det;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(t, ab);
            float v = Vector3.Dot(direction, q) / det;
            if (v < 0f || u + v > 1f) return false;

            distance = Vector3.Dot(ac, q) / det;
            return distance >= 0f;
        }

        // A facet's unit normal, worked out in double precision: Vector3.normalized gives a zero
        // vector for the short cross products of thin slivers, and a zero normal makes the shader's
        // lighting NaN, which bloom spreads into bright flashes. A triangle with no area has none.
        private static bool TryFacetNormal(Vector3 a, Vector3 b, Vector3 c, out Vector3 normal)
        {
            double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z;
            double vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            normal = length > 1e-14 ? new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length)) : Vector3.zero;
            return length > 1e-14;
        }

        // Each vertex's normal: the area-weighted mean of the facets around it, over the whole surface
        // (so the parts' shadow meshes meet without a crack).
        private static Vector3[] SmoothNormals(List<Vector3> vertices, List<int> faces, int faceCount)
        {
            var sums = new Vector3[vertices.Count];
            for (int f = 0; f < faceCount * 3; f += 3)
            {
                Vector3 a = vertices[faces[f]], b = vertices[faces[f + 1]], c = vertices[faces[f + 2]];
                Vector3 weighted = Vector3.Cross(b - a, c - a);
                for (int k = 0; k < 3; k++) sums[faces[f + k]] += weighted;
            }

            var normals = new Vector3[vertices.Count];
            for (int i = 0; i < sums.Length; i++)
            {
                float length = sums[i].magnitude;
                normals[i] = length > 1e-12f ? sums[i] / length : Vector3.up;
            }

            return normals;
        }

        // The triangles with shared corners and smooth normals, the shape that casts the shadow. The
        // caps are left out: they are inside, and their middles have no normal of their own.
        private static Mesh SmoothMesh(List<Vector3> vertices, List<int> faces, List<int> chosen, Vector3[] normals, int surfaceFaceCount)
        {
            var newIndex = new Dictionary<int, int>();
            var corners = new List<Vector3>();
            var cornerNormals = new List<Vector3>();
            var triangles = new List<int>();
            foreach (int f in chosen)
            {
                if (f / 3 >= surfaceFaceCount) continue;
                if (!TryFacetNormal(vertices[faces[f]], vertices[faces[f + 1]], vertices[faces[f + 2]], out _)) continue;

                for (int k = 0; k < 3; k++)
                {
                    int vertex = faces[f + k];
                    if (!newIndex.TryGetValue(vertex, out int index))
                    {
                        index = corners.Count;
                        newIndex[vertex] = index;
                        corners.Add(vertices[vertex]);
                        cornerNormals.Add(normals[vertex]);
                    }

                    triangles.Add(index);
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(corners);
            mesh.SetNormals(cornerNormals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Each triangle with its own three corners, so that it is lit as one flat facet. Triangles
        // with no area are left out: they would be invisible, and have no normal.
        private static Mesh FlatMesh(List<Vector3> vertices, List<int> faces, List<int> chosen)
        {
            var corners = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            foreach (int f in chosen)
            {
                Vector3 a = vertices[faces[f]], b = vertices[faces[f + 1]], c = vertices[faces[f + 2]];
                if (!TryFacetNormal(a, b, c, out Vector3 normal)) continue;

                for (int k = 0; k < 3; k++)
                {
                    triangles.Add(corners.Count);
                    corners.Add(vertices[faces[f + k]]);
                    normals.Add(normal);
                }
            }

            var mesh = new Mesh();
            mesh.SetVertices(corners);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Writes the mesh into the asset at this path, in place if it exists so that references to it survive.
        private static Mesh SaveMesh(string path, string name, Mesh mesh)
        {
            mesh.name = name;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }

            existing.Clear();
            existing.vertices = mesh.vertices;
            existing.normals = mesh.normals;
            existing.triangles = mesh.triangles;
            existing.RecalculateBounds();
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(mesh);
            return existing;
        }

        // Mesh simplification by edge collapse (Garland and Heckbert): each vertex keeps the sum of
        // the planes of the triangles around it (its quadric, weighted by their areas), and the edge
        // whose collapse moves the surface least from those planes goes first. Collapses that would
        // fold a triangle over or pinch the surface are refused. Open edges keep a plane across them,
        // so holes do not grow.
        private sealed class Simplifier
        {
            private readonly List<Vector3d> positions = new List<Vector3d>();
            private readonly List<double[]> quadrics = new List<double[]>();
            private readonly List<List<int>> trianglesOf = new List<List<int>>();
            private readonly List<int> versions = new List<int>();
            private readonly List<bool> removedVertices = new List<bool>();
            private readonly List<int> corners = new List<int>();
            private readonly List<bool> removedTriangles = new List<bool>();
            private readonly Heap heap = new Heap();
            private int liveTriangles;

            public int TriangleCount => liveTriangles;

            public Simplifier(List<Vector3> points, List<int> indices)
            {
                // Corners at the same place (split by the model for its UVs or normals) become one vertex.
                var welded = new Dictionary<(long, long, long), int>();
                var remap = new int[points.Count];
                for (int i = 0; i < points.Count; i++)
                {
                    Vector3 p = points[i];
                    var key = ((long)Math.Round(p.x * 100000.0), (long)Math.Round(p.y * 100000.0), (long)Math.Round(p.z * 100000.0));
                    if (!welded.TryGetValue(key, out int vertex))
                    {
                        vertex = positions.Count;
                        welded[key] = vertex;
                        positions.Add(new Vector3d(p.x, p.y, p.z));
                        quadrics.Add(new double[10]);
                        trianglesOf.Add(new List<int>());
                        versions.Add(0);
                        removedVertices.Add(false);
                    }

                    remap[i] = vertex;
                }

                for (int i = 0; i < indices.Count; i += 3)
                {
                    int a = remap[indices[i]], b = remap[indices[i + 1]], c = remap[indices[i + 2]];
                    if (a == b || b == c || a == c) continue;

                    int t = removedTriangles.Count;
                    corners.Add(a);
                    corners.Add(b);
                    corners.Add(c);
                    removedTriangles.Add(false);
                    trianglesOf[a].Add(t);
                    trianglesOf[b].Add(t);
                    trianglesOf[c].Add(t);
                    liveTriangles++;

                    Vector3d normal = Vector3d.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    double area = normal.Length;
                    if (area < 1e-12) continue;

                    normal /= area;
                    double[] plane = PlaneQuadric(normal, -Vector3d.Dot(normal, positions[a]), area * 0.5);
                    AddTo(quadrics[a], plane);
                    AddTo(quadrics[b], plane);
                    AddTo(quadrics[c], plane);
                }

                // Open edges (in one triangle only): a steep plane through each, along the triangle's normal.
                var edgeUses = new Dictionary<(int, int), int>();
                for (int t = 0; t < removedTriangles.Count; t++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        (int, int) edge = Edge(corners[t * 3 + k], corners[t * 3 + (k + 1) % 3]);
                        edgeUses.TryGetValue(edge, out int uses);
                        edgeUses[edge] = uses + 1;
                    }
                }

                for (int t = 0; t < removedTriangles.Count; t++)
                {
                    int a = corners[t * 3], b = corners[t * 3 + 1], c = corners[t * 3 + 2];
                    Vector3d normal = Vector3d.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    if (normal.Length < 1e-12) continue;

                    normal /= normal.Length;
                    for (int k = 0; k < 3; k++)
                    {
                        int u = corners[t * 3 + k], v = corners[t * 3 + (k + 1) % 3];
                        if (edgeUses[Edge(u, v)] != 1) continue;

                        Vector3d along = positions[v] - positions[u];
                        double edgeLength = along.Length;
                        if (edgeLength < 1e-12) continue;

                        Vector3d across = Vector3d.Cross(along / edgeLength, normal);
                        double[] plane = PlaneQuadric(across, -Vector3d.Dot(across, positions[u]), edgeLength * edgeLength * 100.0);
                        AddTo(quadrics[u], plane);
                        AddTo(quadrics[v], plane);
                    }
                }

                foreach ((int, int) edge in edgeUses.Keys) Push(edge.Item1, edge.Item2);
            }

            public void Reduce(int target)
            {
                while (liveTriangles > target && heap.Count > 0)
                {
                    Heap.Entry entry = heap.Pop();
                    if (removedVertices[entry.U] || removedVertices[entry.V]) continue;
                    if (versions[entry.U] != entry.VersionU || versions[entry.V] != entry.VersionV) continue;

                    TryCollapse(entry.U, entry.V, entry.Target);
                }
            }

            public void Result(out List<Vector3> vertices, out List<int> faces)
            {
                vertices = new List<Vector3>();
                faces = new List<int>();
                var newIndex = new int[positions.Count];
                for (int i = 0; i < positions.Count; i++)
                {
                    newIndex[i] = -1;
                    if (removedVertices[i]) continue;

                    newIndex[i] = vertices.Count;
                    vertices.Add(new Vector3((float)positions[i].X, (float)positions[i].Y, (float)positions[i].Z));
                }

                for (int t = 0; t < removedTriangles.Count; t++)
                {
                    if (removedTriangles[t]) continue;

                    faces.Add(newIndex[corners[t * 3]]);
                    faces.Add(newIndex[corners[t * 3 + 1]]);
                    faces.Add(newIndex[corners[t * 3 + 2]]);
                }
            }

            private void TryCollapse(int u, int v, Vector3d target)
            {
                // The surface must stay a surface: u and v share only the vertices of the triangles on their edge.
                var neighboursOfU = new HashSet<int>();
                foreach (int t in trianglesOf[u])
                {
                    for (int k = 0; k < 3; k++) neighboursOfU.Add(corners[t * 3 + k]);
                }

                int shared = 0;
                int edgeTriangles = 0;
                var counted = new HashSet<int>();
                foreach (int t in trianglesOf[v])
                {
                    bool hasU = false;
                    for (int k = 0; k < 3; k++)
                    {
                        int w = corners[t * 3 + k];
                        if (w == u) hasU = true;
                        if (w != u && w != v && neighboursOfU.Contains(w) && counted.Add(w)) shared++;
                    }

                    if (hasU) edgeTriangles++;
                }

                if (shared > edgeTriangles) return;
                if (Flips(u, v, target) || Flips(v, u, target)) return;

                positions[u] = target;
                AddTo(quadrics[u], quadrics[v]);
                foreach (int t in trianglesOf[v])
                {
                    bool hasU = corners[t * 3] == u || corners[t * 3 + 1] == u || corners[t * 3 + 2] == u;
                    if (hasU)
                    {
                        removedTriangles[t] = true;
                        liveTriangles--;
                        trianglesOf[u].Remove(t);
                        for (int k = 0; k < 3; k++)
                        {
                            int w = corners[t * 3 + k];
                            if (w != u && w != v) trianglesOf[w].Remove(t);
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            if (corners[t * 3 + k] == v) corners[t * 3 + k] = u;
                        }

                        trianglesOf[u].Add(t);
                    }
                }

                trianglesOf[v].Clear();
                removedVertices[v] = true;
                versions[u]++;

                var around = new HashSet<int>();
                foreach (int t in trianglesOf[u])
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int w = corners[t * 3 + k];
                        if (w != u) around.Add(w);
                    }
                }

                // Only u moved: its edges are worked out again, the others still hold.
                foreach (int w in around) Push(u, w);
            }

            // Whether moving vertex `moved` to the target turns over (or nearly flattens) one of its
            // triangles that does not also hold `other`, which disappear.
            private bool Flips(int moved, int other, Vector3d target)
            {
                foreach (int t in trianglesOf[moved])
                {
                    int a = corners[t * 3], b = corners[t * 3 + 1], c = corners[t * 3 + 2];
                    if (a == other || b == other || c == other) continue;

                    Vector3d before = Vector3d.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    Vector3d pa = a == moved ? target : positions[a];
                    Vector3d pb = b == moved ? target : positions[b];
                    Vector3d pc = c == moved ? target : positions[c];
                    Vector3d after = Vector3d.Cross(pb - pa, pc - pa);
                    double lengths = before.Length * after.Length;
                    if (lengths < 1e-18 || Vector3d.Dot(before, after) < 0.2 * lengths) return true;

                    // Nor may it leave a sliver, thinner than before.
                    double after2 = Sliverness(pa, pb, pc);
                    if (after2 < 0.02 && after2 < Sliverness(positions[a], positions[b], positions[c])) return true;
                }

                return false;
            }

            private void Push(int u, int v)
            {
                if (u == v || removedVertices[u] || removedVertices[v]) return;

                var sum = new double[10];
                AddTo(sum, quadrics[u]);
                AddTo(sum, quadrics[v]);
                Vector3d target;
                if (!TrySolve(sum, out target))
                {
                    Vector3d middle = (positions[u] + positions[v]) * 0.5;
                    target = middle;
                    double best = Error(sum, middle);
                    if (Error(sum, positions[u]) < best) { target = positions[u]; best = Error(sum, positions[u]); }
                    if (Error(sum, positions[v]) < best) target = positions[v];
                }
                else
                {
                    // An optimum far off the edge comes from nearly parallel planes: stay near the edge.
                    Vector3d middle = (positions[u] + positions[v]) * 0.5;
                    double reach = (positions[v] - positions[u]).Length;
                    if ((target - middle).Length > reach * 2.0) target = middle;
                }

                heap.Push(new Heap.Entry
                {
                    Cost = Math.Max(0.0, Error(sum, target)),
                    U = u,
                    V = v,
                    VersionU = versions[u],
                    VersionV = versions[v],
                    Target = target
                });
            }

            private static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);

            // How far from a sliver a triangle is: twice its area over the sum of its squared sides,
            // 0.29 for an equilateral triangle, 0 for a flat one.
            private static double Sliverness(Vector3d a, Vector3d b, Vector3d c)
            {
                double sides = (b - a).Length * (b - a).Length + (c - b).Length * (c - b).Length + (a - c).Length * (a - c).Length;
                return sides < 1e-18 ? 0.0 : Vector3d.Cross(b - a, c - a).Length / sides;
            }

            // The quadric of a plane n·p + d = 0, weighted: its symmetric 4 × 4 matrix's upper half.
            private static double[] PlaneQuadric(Vector3d n, double d, double weight)
            {
                return new[]
                {
                    n.X * n.X * weight, n.X * n.Y * weight, n.X * n.Z * weight, n.X * d * weight,
                    n.Y * n.Y * weight, n.Y * n.Z * weight, n.Y * d * weight,
                    n.Z * n.Z * weight, n.Z * d * weight,
                    d * d * weight
                };
            }

            private static void AddTo(double[] sum, double[] quadric)
            {
                for (int i = 0; i < 10; i++) sum[i] += quadric[i];
            }

            private static double Error(double[] q, Vector3d p)
            {
                double x = p.X, y = p.Y, z = p.Z;
                return q[0] * x * x + 2 * q[1] * x * y + 2 * q[2] * x * z + 2 * q[3] * x
                       + q[4] * y * y + 2 * q[5] * y * z + 2 * q[6] * y
                       + q[7] * z * z + 2 * q[8] * z + q[9];
            }

            // The point where the quadric is least: its gradient is zero.
            private static bool TrySolve(double[] q, out Vector3d p)
            {
                double a = q[0], b = q[1], c = q[2], d = q[4], e = q[5], f = q[7];
                double det = a * (d * f - e * e) - b * (b * f - c * e) + c * (b * e - c * d);
                p = default;
                if (Math.Abs(det) < 1e-15) return false;

                double rx = -q[3], ry = -q[6], rz = -q[8];
                double x = (rx * (d * f - e * e) - b * (ry * f - e * rz) + c * (ry * e - d * rz)) / det;
                double y = (a * (ry * f - e * rz) - rx * (b * f - c * e) + c * (b * rz - ry * c)) / det;
                double z = (a * (d * rz - ry * e) - b * (b * rz - ry * c) + rx * (b * e - c * d)) / det;
                p = new Vector3d(x, y, z);
                return true;
            }
        }

        private struct Vector3d
        {
            public readonly double X, Y, Z;

            public Vector3d(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

            public static Vector3d operator +(Vector3d a, Vector3d b) => new Vector3d(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vector3d operator -(Vector3d a, Vector3d b) => new Vector3d(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vector3d operator *(Vector3d a, double k) => new Vector3d(a.X * k, a.Y * k, a.Z * k);
            public static Vector3d operator /(Vector3d a, double k) => new Vector3d(a.X / k, a.Y / k, a.Z / k);
            public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

            public static Vector3d Cross(Vector3d a, Vector3d b) =>
                new Vector3d(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }

        // A binary min-heap of candidate collapses, cheapest first.
        private sealed class Heap
        {
            public struct Entry
            {
                public double Cost;
                public int U, V, VersionU, VersionV;
                public Vector3d Target;
            }

            private readonly List<Entry> items = new List<Entry>();

            public int Count => items.Count;

            public void Push(Entry entry)
            {
                items.Add(entry);
                int i = items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (items[parent].Cost <= items[i].Cost) break;

                    (items[parent], items[i]) = (items[i], items[parent]);
                    i = parent;
                }
            }

            public Entry Pop()
            {
                Entry top = items[0];
                int last = items.Count - 1;
                items[0] = items[last];
                items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1, right = left + 1, smallest = i;
                    if (left < items.Count && items[left].Cost < items[smallest].Cost) smallest = left;
                    if (right < items.Count && items[right].Cost < items[smallest].Cost) smallest = right;
                    if (smallest == i) break;

                    (items[smallest], items[i]) = (items[i], items[smallest]);
                    i = smallest;
                }

                return top;
            }
        }
    }
}
