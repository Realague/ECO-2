using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Voxel.EditorTools
{
    // Builds the plants' models from boxes, in the blocky style of the world, and gives them to the
    // species: four trees (an oak, a tall poplar, a broad umbrella tree, a pale-barked birch) and
    // three bushes (round, wide and low, with berries). Each plant shows one of its species' models,
    // picked from its column (SpeciesAsset.ModelAt).
    //
    // Within a model, the boxes of one material are merged into one mesh, drawn as one part.
    // Textures are 16 × 16 pixels per block, like the block atlas: grey patterns tinted by each
    // material's base colour, which is also the colour the maps use.
    //
    // Run Eco > Rebuild plant models after changing anything below: meshes, textures and materials
    // are rewritten in place, so every reference to them survives.
    public static class PlantModelBuilder
    {
        private const string ModelFolder = "Assets/Species/Models";
        private const string MaterialFolder = "Assets/Materials";
        private const int TexturePixels = 16;

        // A box: its centre and size in blocks (the foot of the plant is the origin), and an optional
        // tilt in degrees around x then z, about its centre.
        private struct Box
        {
            public Vector3 Centre;
            public Vector3 Size;
            public Vector2 Tilt;

            public Box(float x, float y, float z, float sizeX, float sizeY, float sizeZ, float tiltX = 0f, float tiltZ = 0f)
            {
                Centre = new Vector3(x, y, z);
                Size = new Vector3(sizeX, sizeY, sizeZ);
                Tilt = new Vector2(tiltX, tiltZ);
            }
        }

        // The boxes of one material within a model.
        private sealed class Part
        {
            public string Material;
            public Box[] Boxes;
        }

        private sealed class Model
        {
            public string Name;
            public Part[] Parts;
        }

        private sealed class MaterialLook
        {
            public string Name;
            public string Texture;
            public Color Colour;
            public float Smoothness;
        }

        private const string Bark = "PlantTrunk";
        private const string PaleBark = "PlantBarkPale";
        private const string Leaves = "PlantLeaves";
        private const string LightLeaves = "PlantLeavesLight";
        private const string BushLeaves = "PlantBush";
        private const string Berries = "PlantBerry";

        private static readonly MaterialLook[] Materials =
        {
            new MaterialLook { Name = Bark, Texture = "Bark", Colour = new Color(0.44f, 0.31f, 0.19f), Smoothness = 0.1f },
            new MaterialLook { Name = PaleBark, Texture = "Bark", Colour = new Color(0.88f, 0.86f, 0.8f), Smoothness = 0.1f },
            new MaterialLook { Name = Leaves, Texture = "Leaves", Colour = new Color(0.2f, 0.5f, 0.19f), Smoothness = 0.1f },
            new MaterialLook { Name = LightLeaves, Texture = "Leaves", Colour = new Color(0.36f, 0.64f, 0.24f), Smoothness = 0.1f },
            new MaterialLook { Name = BushLeaves, Texture = "Leaves", Colour = new Color(0.33f, 0.54f, 0.2f), Smoothness = 0.1f },
            new MaterialLook { Name = Berries, Texture = null, Colour = new Color(0.78f, 0.13f, 0.15f), Smoothness = 0.5f },
        };

        // Faces of different boxes of one model never lie in the same plane: they would flicker where they overlap.
        private static readonly Model[] Trees =
        {
            new Model
            {
                Name = "Oak",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.8f, 0.4f, 0.8f),
                            new Box(0f, 2.0f, 0f, 0.5f, 3.6f, 0.5f),
                            new Box(0.45f, 3.1f, 0f, 0.25f, 1.3f, 0.25f, tiltZ: -40f),
                            new Box(0f, 3.3f, -0.4f, 0.22f, 1.1f, 0.22f, tiltX: -40f),
                            new Box(-0.35f, 3.45f, 0.2f, 0.2f, 0.9f, 0.2f, tiltZ: 35f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[]
                        {
                            new Box(0f, 4.4f, 0f, 3.0f, 1.6f, 3.0f),
                            new Box(0.2f, 5.4f, -0.1f, 2.2f, 1.0f, 2.2f),
                            new Box(1.3f, 4.0f, 0.3f, 1.4f, 1.2f, 1.6f),
                            new Box(-1.2f, 4.25f, -0.4f, 1.3f, 1.2f, 1.4f),
                            new Box(0.1f, 3.9f, -1.3f, 1.6f, 1.0f, 1.2f),
                            new Box(-0.3f, 4.6f, 1.3f, 1.5f, 1.1f, 1.2f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0.2f, 6.05f, -0.1f, 1.4f, 0.5f, 1.4f),
                            new Box(-0.6f, 5.25f, 0.7f, 1.0f, 0.6f, 1.0f),
                            new Box(1.05f, 5.0f, -0.7f, 1.0f, 0.5f, 0.9f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "Poplar",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.6f, 0.3f, 0.6f),
                            new Box(0f, 2.5f, 0f, 0.4f, 5.0f, 0.4f),
                            new Box(0.25f, 2.6f, 0f, 0.15f, 0.7f, 0.15f, tiltZ: -45f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[]
                        {
                            new Box(0f, 5.2f, 0f, 1.7f, 3.4f, 1.7f),
                            new Box(0.15f, 7.2f, -0.1f, 1.1f, 1.0f, 1.1f),
                            new Box(-0.55f, 4.6f, 0.3f, 0.9f, 1.6f, 1.0f),
                            new Box(0.5f, 5.8f, -0.45f, 0.9f, 1.4f, 0.9f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0.15f, 7.75f, -0.1f, 0.7f, 0.3f, 0.7f),
                            new Box(-0.3f, 6.4f, 0.65f, 0.6f, 0.8f, 0.5f),
                            new Box(0.65f, 5.0f, 0.55f, 0.5f, 0.7f, 0.5f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "Umbrella",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.85f, 0.4f, 0.85f),
                            new Box(0f, 1.6f, 0f, 0.55f, 2.8f, 0.55f),
                            new Box(0.55f, 3.4f, 0.1f, 0.35f, 1.6f, 0.35f, tiltZ: -35f),
                            new Box(-0.5f, 3.5f, -0.2f, 0.3f, 1.5f, 0.3f, tiltZ: 38f),
                            new Box(0.1f, 3.6f, 0.55f, 0.25f, 1.2f, 0.25f, tiltX: 35f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[]
                        {
                            new Box(0.1f, 4.55f, 0f, 4.6f, 0.8f, 3.6f),
                            new Box(-0.2f, 5.2f, 0.2f, 3.2f, 0.7f, 2.6f),
                            new Box(1.6f, 4.35f, -0.9f, 1.4f, 0.9f, 1.5f),
                            new Box(-1.5f, 4.4f, 1.0f, 1.5f, 0.8f, 1.4f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(-0.3f, 5.65f, 0.3f, 1.8f, 0.3f, 1.4f),
                            new Box(1.3f, 5.1f, -0.65f, 1.0f, 0.4f, 1.0f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "Birch",
                Parts = new[]
                {
                    new Part
                    {
                        Material = PaleBark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.5f, 0.3f, 0.5f),
                            new Box(0f, 2.9f, 0f, 0.32f, 5.8f, 0.32f),
                            new Box(-0.25f, 3.9f, 0.1f, 0.14f, 0.9f, 0.14f, tiltZ: 40f),
                            new Box(0.2f, 4.7f, -0.15f, 0.12f, 0.8f, 0.12f, tiltZ: -40f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0.1f, 5.6f, 0.1f, 1.9f, 1.5f, 1.7f),
                            new Box(-0.4f, 6.7f, -0.2f, 1.3f, 1.0f, 1.3f),
                            new Box(0.8f, 4.6f, -0.5f, 1.0f, 1.0f, 1.1f),
                            new Box(-0.75f, 4.9f, 0.6f, 1.0f, 0.9f, 0.9f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[]
                        {
                            new Box(0.1f, 4.95f, 0.1f, 1.7f, 0.3f, 1.5f),
                            new Box(0.85f, 4.2f, -0.5f, 0.8f, 0.35f, 0.9f),
                        }
                    },
                }
            },
        };

        private static readonly Model[] Bushes =
        {
            new Model
            {
                Name = "BushRound",
                Parts = new[]
                {
                    new Part
                    {
                        Material = BushLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 0.3f, 0f, 1.0f, 0.6f, 1.0f),
                            new Box(0.05f, 0.75f, -0.05f, 0.7f, 0.4f, 0.7f),
                            new Box(0.35f, 0.35f, 0.3f, 0.5f, 0.5f, 0.6f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[] { new Box(0.05f, 1.0f, -0.05f, 0.4f, 0.15f, 0.4f) }
                    },
                }
            },
            new Model
            {
                Name = "BushWide",
                Parts = new[]
                {
                    new Part
                    {
                        Material = BushLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 0.22f, 0f, 1.4f, 0.44f, 0.9f),
                            new Box(0.2f, 0.55f, 0.05f, 0.8f, 0.4f, 0.6f),
                            new Box(-0.42f, 0.45f, -0.18f, 0.5f, 0.3f, 0.5f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[] { new Box(0.25f, 0.8f, 0.05f, 0.45f, 0.12f, 0.35f) }
                    },
                }
            },
            new Model
            {
                Name = "BushBerries",
                Parts = new[]
                {
                    new Part
                    {
                        Material = BushLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 0.45f, 0f, 0.8f, 0.9f, 0.8f),
                            new Box(0.15f, 1.0f, 0.1f, 0.5f, 0.3f, 0.5f),
                            new Box(-0.3f, 0.3f, 0.25f, 0.45f, 0.5f, 0.5f),
                        }
                    },
                    new Part
                    {
                        Material = Berries,
                        Boxes = new[]
                        {
                            new Box(0.43f, 0.6f, 0.1f, 0.14f, 0.14f, 0.14f),
                            new Box(0.43f, 0.3f, -0.2f, 0.14f, 0.14f, 0.14f),
                            new Box(-0.1f, 0.5f, 0.43f, 0.14f, 0.14f, 0.14f),
                            new Box(0.2f, 0.75f, 0.43f, 0.14f, 0.14f, 0.14f),
                            new Box(-0.43f, 0.65f, -0.15f, 0.14f, 0.14f, 0.14f),
                            new Box(0f, 0.35f, -0.43f, 0.14f, 0.14f, 0.14f),
                            new Box(0.15f, 1.18f, 0.15f, 0.14f, 0.14f, 0.14f),
                        }
                    },
                }
            },
        };

        [MenuItem("Eco/Rebuild plant models")]
        public static void Build()
        {
            Directory.CreateDirectory(ModelFolder);
            SaveTexture(BarkPixel, $"{ModelFolder}/Bark.png");
            SaveTexture(LeafPixel, $"{ModelFolder}/Leaves.png");
            foreach (MaterialLook look in Materials)
            {
                SaveMaterial(look);
            }

            GiveModels("Assets/Species/Tree.asset", Trees);
            GiveModels("Assets/Species/Bush.asset", Bushes);
            AssetDatabase.SaveAssets();
            Debug.Log($"Plant models rebuilt in {ModelFolder}.");
        }

        // Builds the meshes of each model and writes the models into the species asset.
        private static void GiveModels(string speciesPath, Model[] models)
        {
            var species = AssetDatabase.LoadAssetAtPath<ScriptableObject>(speciesPath);
            var serialized = new SerializedObject(species);
            SerializedProperty modelList = serialized.FindProperty("models");
            modelList.arraySize = models.Length;

            for (int m = 0; m < models.Length; m++)
            {
                SerializedProperty parts = modelList.GetArrayElementAtIndex(m).FindPropertyRelative("parts");
                parts.arraySize = models[m].Parts.Length;
                for (int p = 0; p < models[m].Parts.Length; p++)
                {
                    Part part = models[m].Parts[p];
                    string meshName = $"{models[m].Name}_{part.Material}";
                    Mesh mesh = SaveMesh(BuildMesh(meshName, part.Boxes), $"{ModelFolder}/{meshName}.asset");

                    SerializedProperty entry = parts.GetArrayElementAtIndex(p);
                    entry.FindPropertyRelative("mesh").objectReferenceValue = mesh;
                    entry.FindPropertyRelative("material").objectReferenceValue =
                        AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{part.Material}.mat");
                    entry.FindPropertyRelative("offset").vector3Value = Vector3.zero;
                    entry.FindPropertyRelative("scale").vector3Value = Vector3.one;
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(species);
        }

        private static Mesh BuildMesh(string name, Box[] boxes)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            foreach (Box box in boxes)
            {
                AddBox(box, vertices, normals, uvs, triangles);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // Six faces with their own vertices, so that each face is flat-lit. A face's texture
        // coordinates are its position in blocks within the box, so the texture keeps one tile per
        // block whatever the size of the box.
        private static void AddBox(Box box, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles)
        {
            Quaternion tilt = Quaternion.Euler(box.Tilt.x, 0f, box.Tilt.y);
            Vector3 half = box.Size * 0.5f;

            for (int face = 0; face < 6; face++)
            {
                int axis = face / 2;
                float sign = face % 2 == 0 ? 1f : -1f;

                // Two axes across the face, ordered so that the triangles face outwards.
                Vector3 normal = Vector3.zero;
                normal[axis] = sign;
                Vector3 across = Vector3.zero;
                Vector3 up = Vector3.zero;
                across[(axis + 1) % 3] = 1f;
                up[(axis + 2) % 3] = 1f;
                if (sign < 0f) (across, up) = (up, across);

                // The texture stands upright on the sides (v along y) and lies along x and z on top and bottom.
                int uAxis = axis == 0 ? 2 : 0;
                int vAxis = axis == 1 ? 2 : 1;

                int start = vertices.Count;
                for (int corner = 0; corner < 4; corner++)
                {
                    float a = corner == 1 || corner == 2 ? 1f : -1f;
                    float b = corner >= 2 ? 1f : -1f;
                    Vector3 local = Vector3.Scale(normal + across * a + up * b, half);
                    vertices.Add(box.Centre + tilt * local);
                    normals.Add(tilt * normal);
                    uvs.Add(new Vector2(local[uAxis], local[vAxis]));
                }

                triangles.Add(start);
                triangles.Add(start + 1);
                triangles.Add(start + 2);
                triangles.Add(start);
                triangles.Add(start + 2);
                triangles.Add(start + 3);
            }
        }

        // Rewrites an existing mesh asset in place, so that references to it survive.
        private static Mesh SaveMesh(Mesh mesh, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }

            EditorUtility.CopySerialized(mesh, existing);
            existing.name = Path.GetFileNameWithoutExtension(path);
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(mesh);
            return existing;
        }

        // URP Lit, GPU instancing on (the plant renderer needs it in the asset), tinted, textured or plain.
        private static void SaveMaterial(MaterialLook look)
        {
            string path = $"{MaterialFolder}/{look.Name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = look.Name };
                AssetDatabase.CreateAsset(material, path);
            }

            Texture2D texture = look.Texture == null ? null : AssetDatabase.LoadAssetAtPath<Texture2D>($"{ModelFolder}/{look.Texture}.png");
            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            material.SetColor("_BaseColor", look.Colour);
            material.SetColor("_Color", look.Colour);
            material.SetFloat("_Smoothness", look.Smoothness);
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
        }

        private delegate float Pixel(int x, int y);

        // A grey texture of one block, imported pixel-sharp and repeating.
        private static void SaveTexture(Pixel pixel, string path)
        {
            var texture = new Texture2D(TexturePixels, TexturePixels, TextureFormat.RGBA32, false);
            for (int y = 0; y < TexturePixels; y++)
            {
                for (int x = 0; x < TexturePixels; x++)
                {
                    float value = pixel(x, y);
                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.filterMode = FilterMode.Point;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        // Bark: vertical fibres, each column its own shade, with dark grooves between them.
        private static float BarkPixel(int x, int y)
        {
            float fibre = 0.78f + 0.2f * Noise(x, 0, 1);
            bool groove = x % 4 == 0 && Noise(x, y / 3, 2) > 0.3f;
            return (groove ? 0.62f : fibre) * (0.94f + 0.06f * Noise(x, y, 3));
        }

        // Leaves: a speckle of light and dark leaves, with a few dark gaps.
        private static float LeafPixel(int x, int y)
        {
            float n = Noise(x, y, 4);
            if (n < 0.12f) return 0.6f;
            if (n > 0.88f) return 1f;
            return 0.8f + 0.12f * Noise(x, y, 5);
        }

        private static float Noise(int x, int y, int seed)
        {
            int hash = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791);
            hash ^= hash >> 13;
            hash *= 1274126177;
            return ((hash >> 8) & 0xFF) / 255f;
        }
    }
}
