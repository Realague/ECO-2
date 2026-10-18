using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Voxel.EditorTools
{
    // Builds the plants' models from boxes, in the blocky style of the world, and gives them to the
    // species (Assets/Species): oaks, birches, poplars, acacias and pines, bushes, tufts of green and
    // of dry grass, cacti and reeds, jungle trees, palms, ferns, willows, swamp cypresses and water
    // lilies, two or three models each. Each plant shows one of its species'
    // models, picked from its column (SpeciesAsset.ModelAt).
    //
    // Within a model, the boxes of one material are merged into one mesh, drawn as one part.
    // Textures are 64 × 64 pixels per block, like the block atlas: grey patterns tinted by each
    // material's base colour, which is also the colour the maps use.
    //
    // Run Eco > Rebuild plant models after changing anything below: meshes, textures and materials
    // are rewritten in place, so every reference to them survives. A species asset that does not
    // exist yet is created with default rules, to be set in the inspector.
    public static class PlantModelBuilder
    {
        private const string SpeciesFolder = "Assets/Species";
        private const string ModelFolder = "Assets/Species/Models";
        private const string MaterialFolder = "Assets/Materials";
        private const int TexturePixels = 64;

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
        private const string DryLeaves = "PlantLeavesDry";
        private const string Needles = "PlantNeedles";
        private const string BushLeaves = "PlantBush";
        private const string Berries = "PlantBerry";
        private const string GrassBlades = "PlantGrass";
        private const string Straw = "PlantStraw";
        private const string CactusSkin = "PlantCactus";
        private const string ReedStalk = "PlantReed";
        private const string ReedHead = "PlantReedHead";
        private const string JungleLeaves = "PlantLeavesJungle";
        private const string GreyBark = "PlantBarkGrey";
        private const string PalmTrunk = "PlantPalmTrunk";
        private const string Frond = "PlantFrond";
        private const string FernLeaves = "PlantFern";
        private const string WillowLeaves = "PlantWillow";
        private const string CypressBark = "PlantCypressBark";
        private const string CypressLeaves = "PlantCypress";
        private const string LilyPad = "PlantLily";
        private const string LilyFlower = "PlantLilyFlower";

        private static readonly MaterialLook[] Materials =
        {
            new MaterialLook { Name = Bark, Texture = "Bark", Colour = new Color(0.44f, 0.31f, 0.19f), Smoothness = 0.1f },
            new MaterialLook { Name = PaleBark, Texture = "Bark", Colour = new Color(0.88f, 0.86f, 0.8f), Smoothness = 0.1f },
            new MaterialLook { Name = Leaves, Texture = "Leaves", Colour = new Color(0.2f, 0.5f, 0.19f), Smoothness = 0.1f },
            new MaterialLook { Name = LightLeaves, Texture = "Leaves", Colour = new Color(0.36f, 0.64f, 0.24f), Smoothness = 0.1f },
            new MaterialLook { Name = DryLeaves, Texture = "Leaves", Colour = new Color(0.47f, 0.52f, 0.22f), Smoothness = 0.1f },
            new MaterialLook { Name = Needles, Texture = "Leaves", Colour = new Color(0.12f, 0.32f, 0.2f), Smoothness = 0.1f },
            new MaterialLook { Name = BushLeaves, Texture = "Leaves", Colour = new Color(0.33f, 0.54f, 0.2f), Smoothness = 0.1f },
            new MaterialLook { Name = Berries, Texture = null, Colour = new Color(0.78f, 0.13f, 0.15f), Smoothness = 0.5f },
            new MaterialLook { Name = GrassBlades, Texture = "Leaves", Colour = new Color(0.4f, 0.66f, 0.24f), Smoothness = 0.1f },
            new MaterialLook { Name = Straw, Texture = "Leaves", Colour = new Color(0.8f, 0.7f, 0.4f), Smoothness = 0.1f },
            new MaterialLook { Name = CactusSkin, Texture = "Bark", Colour = new Color(0.3f, 0.55f, 0.27f), Smoothness = 0.3f },
            new MaterialLook { Name = ReedStalk, Texture = "Bark", Colour = new Color(0.5f, 0.62f, 0.3f), Smoothness = 0.1f },
            new MaterialLook { Name = ReedHead, Texture = null, Colour = new Color(0.42f, 0.27f, 0.15f), Smoothness = 0.1f },
            new MaterialLook { Name = JungleLeaves, Texture = "Leaves", Colour = new Color(0.1f, 0.42f, 0.14f), Smoothness = 0.15f },
            new MaterialLook { Name = GreyBark, Texture = "Bark", Colour = new Color(0.58f, 0.55f, 0.47f), Smoothness = 0.1f },
            new MaterialLook { Name = PalmTrunk, Texture = "Bark", Colour = new Color(0.6f, 0.49f, 0.33f), Smoothness = 0.1f },
            new MaterialLook { Name = Frond, Texture = "Leaves", Colour = new Color(0.3f, 0.58f, 0.2f), Smoothness = 0.15f },
            new MaterialLook { Name = FernLeaves, Texture = "Leaves", Colour = new Color(0.2f, 0.52f, 0.2f), Smoothness = 0.1f },
            new MaterialLook { Name = WillowLeaves, Texture = "Leaves", Colour = new Color(0.46f, 0.58f, 0.31f), Smoothness = 0.1f },
            new MaterialLook { Name = CypressBark, Texture = "Bark", Colour = new Color(0.52f, 0.36f, 0.27f), Smoothness = 0.1f },
            new MaterialLook { Name = CypressLeaves, Texture = "Leaves", Colour = new Color(0.26f, 0.42f, 0.2f), Smoothness = 0.1f },
            new MaterialLook { Name = LilyPad, Texture = "Leaves", Colour = new Color(0.24f, 0.52f, 0.2f), Smoothness = 0.4f },
            new MaterialLook { Name = LilyFlower, Texture = null, Colour = new Color(0.96f, 0.82f, 0.88f), Smoothness = 0.3f },
        };

        // Faces of different boxes of one model never lie in the same plane: they would flicker where they overlap.

        private static readonly Model[] Oaks =
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
                Name = "OakBroad",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.9f, 0.4f, 0.9f),
                            new Box(0f, 1.6f, 0f, 0.6f, 2.8f, 0.6f),
                            new Box(0.5f, 2.7f, 0.1f, 0.28f, 1.2f, 0.28f, tiltZ: -45f),
                            new Box(-0.45f, 2.8f, -0.2f, 0.26f, 1.1f, 0.26f, tiltZ: 45f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[]
                        {
                            new Box(0f, 3.7f, 0f, 3.8f, 1.4f, 3.4f),
                            new Box(0.1f, 4.6f, 0.1f, 2.6f, 0.9f, 2.4f),
                            new Box(1.6f, 3.4f, 0.4f, 1.2f, 1.0f, 1.6f),
                            new Box(-1.7f, 3.5f, -0.3f, 1.1f, 0.9f, 1.5f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0.15f, 5.2f, 0.05f, 1.5f, 0.4f, 1.3f),
                            new Box(-1.0f, 4.3f, 0.9f, 1.1f, 0.5f, 0.9f),
                        }
                    },
                }
            },
        };

        private static readonly Model[] Poplars =
        {
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
                Name = "PoplarSlim",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.55f, 0.3f, 0.55f),
                            new Box(0f, 2.9f, 0f, 0.36f, 5.8f, 0.36f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[]
                        {
                            new Box(0f, 5.9f, 0f, 1.3f, 4.2f, 1.3f),
                            new Box(0.1f, 8.3f, 0.05f, 0.8f, 0.9f, 0.8f),
                            new Box(-0.4f, 5.2f, 0.25f, 0.7f, 1.8f, 0.8f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0.1f, 8.85f, 0.05f, 0.5f, 0.25f, 0.5f),
                            new Box(0.45f, 6.6f, -0.3f, 0.55f, 1.0f, 0.5f),
                        }
                    },
                }
            },
        };

        private static readonly Model[] Birches =
        {
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
            new Model
            {
                Name = "BirchTwin",
                Parts = new[]
                {
                    new Part
                    {
                        Material = PaleBark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.6f, 0.3f, 0.45f),
                            new Box(-0.17f, 2.6f, 0f, 0.28f, 5.2f, 0.28f, tiltZ: 4f),
                            new Box(0.2f, 2.3f, 0.05f, 0.26f, 4.6f, 0.26f, tiltZ: -5f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(-0.35f, 5.4f, 0f, 1.6f, 1.3f, 1.5f),
                            new Box(0.55f, 4.9f, 0.1f, 1.3f, 1.1f, 1.2f),
                            new Box(-0.1f, 6.3f, -0.05f, 1.0f, 0.8f, 1.0f),
                        }
                    },
                    new Part
                    {
                        Material = Leaves,
                        Boxes = new[] { new Box(0.6f, 4.25f, 0.15f, 1.1f, 0.25f, 1.0f) }
                    },
                }
            },
        };

        private static readonly Model[] Acacias =
        {
            new Model
            {
                Name = "Acacia",
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
                        Material = DryLeaves,
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
                Name = "AcaciaWide",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.8f, 0.4f, 0.8f),
                            new Box(0.2f, 1.4f, 0f, 0.5f, 2.6f, 0.5f, tiltZ: -12f),
                            new Box(-0.4f, 2.9f, 0.1f, 0.3f, 1.4f, 0.3f, tiltZ: 40f),
                            new Box(0.75f, 3.0f, -0.2f, 0.3f, 1.3f, 0.3f, tiltZ: -35f),
                        }
                    },
                    new Part
                    {
                        Material = DryLeaves,
                        Boxes = new[]
                        {
                            new Box(0.3f, 3.75f, 0f, 5.2f, 0.6f, 4.0f),
                            new Box(0.1f, 4.25f, 0.15f, 3.4f, 0.5f, 2.8f),
                            new Box(-1.9f, 3.6f, 0.9f, 1.4f, 0.7f, 1.3f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[] { new Box(0.2f, 4.6f, 0.2f, 2.0f, 0.25f, 1.6f) }
                    },
                }
            },
        };

        private static readonly Model[] Pines =
        {
            new Model
            {
                Name = "Pine",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.6f, 0.3f, 0.6f),
                            new Box(0f, 3.0f, 0f, 0.4f, 6.0f, 0.4f),
                        }
                    },
                    new Part
                    {
                        Material = Needles,
                        Boxes = new[]
                        {
                            new Box(0f, 1.6f, 0f, 3.0f, 0.9f, 3.0f),
                            new Box(0.05f, 2.55f, -0.05f, 2.5f, 0.85f, 2.5f),
                            new Box(-0.05f, 3.45f, 0.05f, 2.0f, 0.8f, 2.0f),
                            new Box(0f, 4.3f, 0f, 1.5f, 0.75f, 1.5f),
                            new Box(0.05f, 5.1f, 0f, 1.05f, 0.7f, 1.05f),
                            new Box(0f, 5.85f, 0.05f, 0.6f, 0.7f, 0.6f),
                            new Box(0f, 6.5f, 0f, 0.25f, 0.5f, 0.25f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "PineTall",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.55f, 0.3f, 0.55f),
                            new Box(0f, 3.6f, 0f, 0.38f, 7.2f, 0.38f),
                        }
                    },
                    new Part
                    {
                        Material = Needles,
                        Boxes = new[]
                        {
                            new Box(0f, 2.2f, 0f, 2.4f, 1.0f, 2.4f),
                            new Box(0.05f, 3.25f, 0f, 2.0f, 0.9f, 2.0f),
                            new Box(0f, 4.2f, 0.05f, 1.6f, 0.85f, 1.6f),
                            new Box(0.05f, 5.1f, 0f, 1.25f, 0.8f, 1.25f),
                            new Box(0f, 5.95f, 0f, 0.9f, 0.75f, 0.9f),
                            new Box(0.05f, 6.75f, 0.05f, 0.55f, 0.7f, 0.55f),
                            new Box(0f, 7.4f, 0f, 0.22f, 0.5f, 0.22f),
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

        // Tufts: a few thin blades leaning outwards, green or dry.
        private static readonly Box[] Tuft =
        {
            new Box(0f, 0.25f, 0f, 0.12f, 0.5f, 0.12f),
            new Box(0.18f, 0.2f, 0.1f, 0.1f, 0.4f, 0.1f, tiltZ: -15f),
            new Box(-0.15f, 0.22f, -0.08f, 0.1f, 0.44f, 0.1f, tiltZ: 18f),
            new Box(0.05f, 0.18f, -0.2f, 0.1f, 0.36f, 0.1f, tiltX: -20f),
            new Box(-0.05f, 0.21f, 0.2f, 0.1f, 0.42f, 0.1f, tiltX: 18f),
        };

        private static readonly Box[] TallTuft =
        {
            new Box(0f, 0.35f, 0f, 0.12f, 0.7f, 0.12f),
            new Box(0.2f, 0.28f, 0.05f, 0.1f, 0.56f, 0.1f, tiltZ: -14f),
            new Box(-0.18f, 0.3f, -0.1f, 0.1f, 0.6f, 0.1f, tiltZ: 16f),
            new Box(0.08f, 0.25f, 0.22f, 0.1f, 0.5f, 0.1f, tiltX: 16f),
            new Box(-0.1f, 0.24f, -0.22f, 0.1f, 0.48f, 0.1f, tiltX: -15f),
            new Box(0.25f, 0.15f, -0.2f, 0.08f, 0.3f, 0.08f, tiltZ: -25f),
        };

        private static readonly Model[] Grasses =
        {
            new Model { Name = "Tuft", Parts = new[] { new Part { Material = GrassBlades, Boxes = Tuft } } },
            new Model { Name = "TuftTall", Parts = new[] { new Part { Material = GrassBlades, Boxes = TallTuft } } },
        };

        private static readonly Model[] DryGrasses =
        {
            new Model { Name = "DryTuft", Parts = new[] { new Part { Material = Straw, Boxes = Tuft } } },
            new Model { Name = "DryTuftTall", Parts = new[] { new Part { Material = Straw, Boxes = TallTuft } } },
        };

        private static readonly Model[] Cacti =
        {
            new Model
            {
                Name = "Cactus",
                Parts = new[] { new Part { Material = CactusSkin, Boxes = new[] { new Box(0f, 0.9f, 0f, 0.5f, 1.8f, 0.5f) } } }
            },
            new Model
            {
                Name = "CactusArms",
                Parts = new[]
                {
                    new Part
                    {
                        Material = CactusSkin,
                        Boxes = new[]
                        {
                            new Box(0f, 1.2f, 0f, 0.55f, 2.4f, 0.55f),
                            new Box(0.45f, 1.0f, 0f, 0.4f, 0.28f, 0.3f),
                            new Box(0.62f, 1.45f, 0f, 0.26f, 0.7f, 0.26f),
                            new Box(-0.42f, 1.5f, 0.02f, 0.32f, 0.26f, 0.28f),
                            new Box(-0.56f, 1.85f, 0.02f, 0.24f, 0.55f, 0.24f),
                        }
                    }
                }
            },
        };

        private static readonly Model[] Reeds =
        {
            new Model
            {
                Name = "Reeds",
                Parts = new[]
                {
                    new Part
                    {
                        Material = ReedStalk,
                        Boxes = new[]
                        {
                            new Box(0f, 0.6f, 0f, 0.07f, 1.2f, 0.07f),
                            new Box(0.18f, 0.5f, 0.1f, 0.06f, 1.0f, 0.06f),
                            new Box(-0.15f, 0.55f, -0.1f, 0.06f, 1.1f, 0.06f),
                            new Box(0.05f, 0.42f, -0.2f, 0.06f, 0.84f, 0.06f),
                            new Box(-0.1f, 0.48f, 0.19f, 0.06f, 0.96f, 0.06f),
                        }
                    },
                    new Part
                    {
                        Material = ReedHead,
                        Boxes = new[]
                        {
                            new Box(0f, 1.31f, 0f, 0.11f, 0.26f, 0.11f),
                            new Box(0.18f, 1.1f, 0.1f, 0.1f, 0.24f, 0.1f),
                            new Box(-0.15f, 1.21f, -0.1f, 0.1f, 0.25f, 0.1f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "ReedsSparse",
                Parts = new[]
                {
                    new Part
                    {
                        Material = ReedStalk,
                        Boxes = new[]
                        {
                            new Box(0.1f, 0.55f, 0.05f, 0.07f, 1.1f, 0.07f),
                            new Box(-0.17f, 0.45f, 0.12f, 0.06f, 0.9f, 0.06f),
                            new Box(0.02f, 0.4f, -0.18f, 0.06f, 0.8f, 0.06f),
                        }
                    },
                    new Part
                    {
                        Material = ReedHead,
                        Boxes = new[] { new Box(0.1f, 1.21f, 0.05f, 0.11f, 0.26f, 0.11f) }
                    },
                }
            },
        };

        // Emergent trees of the jungle: a tall bare trunk on buttress roots, a few branches near the
        // top and a wide flat crown well above the rest of the forest.
        private static readonly Model[] JungleTrees =
        {
            new Model
            {
                Name = "JungleTree",
                Parts = new[]
                {
                    new Part
                    {
                        Material = GreyBark,
                        Boxes = new[]
                        {
                            new Box(0f, 7f, 0f, 0.9f, 14f, 0.9f),
                            new Box(0.62f, 0.75f, 0.03f, 0.84f, 1.5f, 0.16f),
                            new Box(-0.6f, 0.7f, -0.04f, 0.8f, 1.4f, 0.17f),
                            new Box(0.02f, 0.8f, 0.61f, 0.15f, 1.6f, 0.82f),
                            new Box(-0.03f, 0.65f, -0.58f, 0.18f, 1.3f, 0.76f),
                            new Box(1.0f, 13.6f, 0.1f, 0.35f, 2.4f, 0.35f, tiltZ: -55f),
                            new Box(-0.9f, 13.9f, -0.2f, 0.32f, 2.2f, 0.32f, tiltZ: 50f),
                            new Box(0.1f, 14.1f, 0.9f, 0.3f, 2.0f, 0.3f, tiltX: 50f),
                        }
                    },
                    new Part
                    {
                        Material = JungleLeaves,
                        Boxes = new[]
                        {
                            new Box(0.2f, 15.2f, 0f, 6.2f, 1.4f, 5.6f),
                            new Box(-0.1f, 16.3f, 0.2f, 4.4f, 1.1f, 4.0f),
                            new Box(2.6f, 14.7f, -1.2f, 2.2f, 1.1f, 2.4f),
                            new Box(-2.5f, 14.9f, 1.4f, 2.4f, 1.0f, 2.2f),
                            new Box(0.4f, 14.55f, -2.7f, 2.6f, 0.9f, 1.8f),
                            new Box(0.65f, 8.3f, 0.2f, 0.7f, 0.6f, 0.9f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 17.05f, 0.1f, 2.4f, 0.45f, 2.2f),
                            new Box(1.8f, 15.95f, 1.3f, 1.6f, 0.5f, 1.5f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "JungleTreeTall",
                Parts = new[]
                {
                    new Part
                    {
                        Material = GreyBark,
                        Boxes = new[]
                        {
                            new Box(0f, 8.5f, 0f, 0.8f, 17f, 0.8f),
                            new Box(0.57f, 0.85f, -0.02f, 0.76f, 1.7f, 0.15f),
                            new Box(-0.3f, 0.7f, 0.5f, 0.14f, 1.4f, 0.7f),
                            new Box(-0.28f, 0.75f, -0.52f, 0.62f, 1.5f, 0.13f),
                            new Box(0.85f, 11.6f, 0.2f, 0.26f, 1.8f, 0.26f, tiltZ: -60f),
                            new Box(-0.8f, 12.3f, -0.3f, 0.27f, 1.6f, 0.27f, tiltZ: 60f),
                        }
                    },
                    new Part
                    {
                        Material = JungleLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 17.6f, 0f, 5.0f, 1.2f, 5.4f),
                            new Box(0.3f, 18.5f, -0.2f, 3.2f, 0.9f, 3.4f),
                            new Box(1.6f, 12.2f, 0.4f, 2.6f, 0.8f, 2.2f),
                            new Box(-1.5f, 12.9f, -0.6f, 2.2f, 0.85f, 2.4f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[]
                        {
                            new Box(0.25f, 19.15f, -0.1f, 1.8f, 0.4f, 1.9f),
                            new Box(-1.6f, 18.05f, 1.5f, 1.3f, 0.45f, 1.2f),
                        }
                    },
                }
            },
        };

        // Palms: a trunk of segments bending a little more at each, four drooping fronds, coconuts.
        private static readonly Model[] Palms =
        {
            new Model
            {
                Name = "Palm",
                Parts = new[]
                {
                    new Part
                    {
                        Material = PalmTrunk,
                        Boxes = new[]
                        {
                            new Box(0f, 0.9f, 0f, 0.45f, 1.8f, 0.45f),
                            new Box(0.12f, 2.6f, 0f, 0.42f, 1.8f, 0.42f, tiltZ: -8f),
                            new Box(0.38f, 4.3f, 0f, 0.4f, 1.8f, 0.4f, tiltZ: -12f),
                            new Box(0.72f, 5.9f, 0f, 0.38f, 1.6f, 0.38f, tiltZ: -16f),
                        }
                    },
                    new Part
                    {
                        Material = Frond,
                        Boxes = new[]
                        {
                            new Box(1.0f, 7.0f, 0f, 0.9f, 0.5f, 0.9f),
                            new Box(2.25f, 6.6f, 0f, 2.4f, 0.12f, 0.6f, tiltZ: -22f),
                            new Box(-0.25f, 6.65f, 0.05f, 2.4f, 0.13f, 0.62f, tiltZ: 22f),
                            new Box(1.0f, 6.62f, 1.25f, 0.58f, 0.12f, 2.4f, tiltX: 22f),
                            new Box(1.05f, 6.58f, -1.25f, 0.6f, 0.13f, 2.3f, tiltX: -24f),
                        }
                    },
                    new Part
                    {
                        Material = ReedHead,
                        Boxes = new[]
                        {
                            new Box(0.82f, 6.55f, 0.22f, 0.22f, 0.22f, 0.22f),
                            new Box(1.12f, 6.5f, -0.17f, 0.21f, 0.21f, 0.21f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "PalmTall",
                Parts = new[]
                {
                    new Part
                    {
                        Material = PalmTrunk,
                        Boxes = new[]
                        {
                            new Box(0f, 1.0f, 0f, 0.44f, 2.0f, 0.44f),
                            new Box(-0.05f, 3.0f, 0.06f, 0.41f, 2.0f, 0.41f, tiltX: 4f),
                            new Box(-0.12f, 5.0f, 0.2f, 0.39f, 2.0f, 0.39f, tiltX: 6f),
                            new Box(-0.2f, 6.9f, 0.38f, 0.37f, 1.8f, 0.37f, tiltX: 8f),
                        }
                    },
                    new Part
                    {
                        Material = Frond,
                        Boxes = new[]
                        {
                            new Box(-0.25f, 8.05f, 0.5f, 0.85f, 0.45f, 0.85f),
                            new Box(1.0f, 7.7f, 0.5f, 2.5f, 0.12f, 0.58f, tiltZ: -26f),
                            new Box(-1.5f, 7.72f, 0.52f, 2.4f, 0.13f, 0.6f, tiltZ: 24f),
                            new Box(-0.25f, 7.68f, 1.8f, 0.6f, 0.12f, 2.5f, tiltX: 26f),
                            new Box(-0.22f, 7.66f, -0.75f, 0.58f, 0.13f, 2.4f, tiltX: -22f),
                            new Box(-0.2f, 8.4f, 0.55f, 0.16f, 0.6f, 0.16f),
                        }
                    },
                    new Part
                    {
                        Material = ReedHead,
                        Boxes = new[] { new Box(-0.05f, 7.6f, 0.55f, 0.22f, 0.22f, 0.22f) }
                    },
                }
            },
        };

        // Ferns of the forest floor: fronds arching out of a crown.
        private static readonly Model[] Ferns =
        {
            new Model
            {
                Name = "Fern",
                Parts = new[]
                {
                    new Part
                    {
                        Material = FernLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 0.15f, 0f, 0.14f, 0.3f, 0.14f),
                            new Box(0.32f, 0.3f, 0.02f, 0.62f, 0.06f, 0.22f, tiltZ: 32f),
                            new Box(-0.3f, 0.28f, -0.03f, 0.6f, 0.07f, 0.21f, tiltZ: -34f),
                            new Box(0.03f, 0.32f, 0.31f, 0.2f, 0.06f, 0.64f, tiltX: -30f),
                            new Box(-0.02f, 0.26f, -0.29f, 0.23f, 0.07f, 0.58f, tiltX: 36f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "FernLarge",
                Parts = new[]
                {
                    new Part
                    {
                        Material = FernLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.16f, 0.4f, 0.16f),
                            new Box(0.42f, 0.42f, 0.05f, 0.82f, 0.06f, 0.26f, tiltZ: 38f),
                            new Box(-0.4f, 0.4f, -0.04f, 0.8f, 0.07f, 0.25f, tiltZ: -40f),
                            new Box(0.04f, 0.44f, 0.41f, 0.24f, 0.06f, 0.84f, tiltX: -36f),
                            new Box(-0.05f, 0.38f, -0.4f, 0.27f, 0.07f, 0.78f, tiltX: 42f),
                            new Box(0.3f, 0.22f, -0.28f, 0.5f, 0.05f, 0.19f, tiltZ: 18f),
                            new Box(-0.27f, 0.2f, 0.3f, 0.18f, 0.05f, 0.52f, tiltX: -16f),
                        }
                    },
                    new Part
                    {
                        Material = GrassBlades,
                        Boxes = new[] { new Box(0.02f, 0.55f, 0.01f, 0.09f, 0.3f, 0.09f) }
                    },
                }
            },
        };

        // Weeping willows: a short thick trunk, a round crown and curtains of leaves hanging to the ground.
        private static readonly Model[] Willows =
        {
            new Model
            {
                Name = "Willow",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.9f, 0.4f, 0.9f),
                            new Box(0f, 1.6f, 0f, 0.65f, 3.2f, 0.65f),
                            new Box(0.55f, 3.3f, 0.1f, 0.3f, 1.3f, 0.3f, tiltZ: -45f),
                            new Box(-0.5f, 3.4f, -0.15f, 0.28f, 1.2f, 0.28f, tiltZ: 42f),
                        }
                    },
                    new Part
                    {
                        Material = WillowLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 4.3f, 0f, 3.4f, 1.0f, 3.4f),
                            new Box(0.1f, 5.0f, -0.05f, 2.4f, 0.6f, 2.3f),
                            new Box(1.82f, 3.1f, 0f, 0.26f, 2.2f, 3.0f),
                            new Box(-1.8f, 2.95f, 0.05f, 0.24f, 2.5f, 2.9f),
                            new Box(0f, 3.15f, 1.81f, 3.1f, 2.1f, 0.25f),
                            new Box(0.05f, 3.0f, -1.83f, 2.8f, 2.3f, 0.27f),
                        }
                    },
                    new Part
                    {
                        Material = LightLeaves,
                        Boxes = new[] { new Box(0.2f, 5.45f, 0.1f, 1.6f, 0.35f, 1.5f) }
                    },
                }
            },
            new Model
            {
                Name = "WillowLeaning",
                Parts = new[]
                {
                    new Part
                    {
                        Material = Bark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.2f, 0f, 0.85f, 0.4f, 0.85f),
                            new Box(0.3f, 1.5f, 0f, 0.6f, 2.8f, 0.6f, tiltZ: -14f),
                            new Box(0.95f, 3.0f, 0.1f, 0.3f, 1.2f, 0.3f, tiltZ: -40f),
                        }
                    },
                    new Part
                    {
                        Material = WillowLeaves,
                        Boxes = new[]
                        {
                            new Box(0.7f, 3.9f, 0f, 3.6f, 0.9f, 3.0f),
                            new Box(0.8f, 4.55f, 0.1f, 2.2f, 0.55f, 2.0f),
                            new Box(2.6f, 2.7f, 0.05f, 0.24f, 2.2f, 2.6f),
                            new Box(-1.15f, 2.85f, -0.05f, 0.25f, 1.9f, 2.5f),
                            new Box(0.65f, 2.8f, 1.58f, 3.2f, 2.0f, 0.24f),
                            new Box(0.75f, 2.65f, -1.57f, 3.0f, 2.3f, 0.26f),
                        }
                    },
                }
            },
        };

        // Bald cypresses: a flared base with knees about it, a straight trunk and tiers of feathery
        // foliage narrowing upwards. They stand in shallow water as well as on mud.
        private static readonly Model[] Cypresses =
        {
            new Model
            {
                Name = "Cypress",
                Parts = new[]
                {
                    new Part
                    {
                        Material = CypressBark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.5f, 0f, 1.1f, 1.0f, 1.1f),
                            new Box(0f, 4.0f, 0f, 0.5f, 7.0f, 0.5f),
                            new Box(0.95f, 0.3f, 0.35f, 0.2f, 0.6f, 0.2f),
                            new Box(-0.8f, 0.25f, -0.6f, 0.18f, 0.5f, 0.18f),
                            new Box(0.4f, 0.35f, -1.0f, 0.19f, 0.7f, 0.19f),
                        }
                    },
                    new Part
                    {
                        Material = CypressLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 4.2f, 0f, 2.8f, 0.7f, 2.6f),
                            new Box(0.1f, 5.3f, -0.05f, 2.3f, 0.75f, 2.2f),
                            new Box(-0.05f, 6.4f, 0.05f, 1.8f, 0.8f, 1.7f),
                            new Box(0.05f, 7.45f, 0f, 1.3f, 0.85f, 1.25f),
                            new Box(0f, 8.3f, 0.02f, 0.75f, 0.6f, 0.7f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "CypressFlat",
                Parts = new[]
                {
                    new Part
                    {
                        Material = CypressBark,
                        Boxes = new[]
                        {
                            new Box(0f, 0.55f, 0f, 1.2f, 1.1f, 1.2f),
                            new Box(0f, 3.6f, 0f, 0.55f, 6.2f, 0.55f),
                            new Box(-1.0f, 0.3f, 0.3f, 0.2f, 0.6f, 0.2f),
                            new Box(0.7f, 0.28f, 0.8f, 0.17f, 0.55f, 0.17f),
                            new Box(0.5f, 6.2f, 0.1f, 0.25f, 1.3f, 0.25f, tiltZ: -45f),
                        }
                    },
                    new Part
                    {
                        Material = CypressLeaves,
                        Boxes = new[]
                        {
                            new Box(0f, 5.0f, 0f, 2.2f, 0.7f, 2.0f),
                            new Box(0.2f, 6.9f, 0.1f, 3.6f, 0.8f, 3.2f),
                            new Box(0.1f, 7.6f, -0.1f, 2.6f, 0.6f, 2.4f),
                        }
                    },
                }
            },
        };

        // Water lilies: flat pads lying on the water (drawn at its surface: SpeciesAsset.floatsOnWater), one flowering.
        private static readonly Model[] LilyPads =
        {
            new Model
            {
                Name = "LilyPad",
                Parts = new[]
                {
                    new Part
                    {
                        Material = LilyPad,
                        Boxes = new[]
                        {
                            new Box(0f, 0.03f, 0f, 0.62f, 0.04f, 0.56f),
                            new Box(0.32f, 0.04f, 0.3f, 0.36f, 0.05f, 0.4f),
                            new Box(-0.28f, 0.037f, -0.25f, 0.34f, 0.03f, 0.3f),
                        }
                    },
                    new Part
                    {
                        Material = LilyFlower,
                        Boxes = new[]
                        {
                            new Box(0.06f, 0.125f, -0.04f, 0.18f, 0.14f, 0.18f),
                            new Box(0.06f, 0.145f, -0.04f, 0.26f, 0.06f, 0.08f),
                        }
                    },
                }
            },
            new Model
            {
                Name = "LilyPads",
                Parts = new[]
                {
                    new Part
                    {
                        Material = LilyPad,
                        Boxes = new[]
                        {
                            new Box(-0.1f, 0.03f, 0.05f, 0.5f, 0.04f, 0.48f),
                            new Box(0.3f, 0.04f, -0.28f, 0.42f, 0.05f, 0.38f),
                            new Box(0.25f, 0.037f, 0.33f, 0.3f, 0.03f, 0.28f),
                            new Box(-0.33f, 0.046f, -0.3f, 0.26f, 0.04f, 0.26f),
                        }
                    },
                }
            },
        };

        // Each species asset and its models.
        private static readonly (string Species, Model[] Models)[] SpeciesModels =
        {
            ("Oak", Oaks),
            ("Bush", Bushes),
            ("Birch", Birches),
            ("Pine", Pines),
            ("Acacia", Acacias),
            ("Poplar", Poplars),
            ("TallGrass", Grasses),
            ("DryGrass", DryGrasses),
            ("Cactus", Cacti),
            ("Reeds", Reeds),
            ("JungleTree", JungleTrees),
            ("Palm", Palms),
            ("Fern", Ferns),
            ("Willow", Willows),
            ("Cypress", Cypresses),
            ("LilyPad", LilyPads),
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

            foreach ((string species, Model[] models) in SpeciesModels)
            {
                GiveModels($"{SpeciesFolder}/{species}.asset", models);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Plant models rebuilt in {ModelFolder}.");
        }

        // Builds the meshes of each model and writes the models into the species asset.
        private static void GiveModels(string speciesPath, Model[] models)
        {
            var species = AssetDatabase.LoadAssetAtPath<ScriptableObject>(speciesPath);
            if (species == null)
            {
                species = ScriptableObject.CreateInstance<Voxel.Client.SpeciesAsset>();
                AssetDatabase.CreateAsset(species, speciesPath);
            }

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

        // A grey texture of one block, imported pixel-sharp and repeating, with mipmaps so that it
        // does not shimmer far away.
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
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        // Bark: vertical fibres two pixels wide, each its own shade, dark grooves every sixteen
        // pixels (broken in places), and a few short cracks across.
        private static float BarkPixel(int x, int y)
        {
            float fibre = 0.78f + 0.2f * Noise(x / 2, 0, 1);
            bool groove = x % 16 < 2 && Noise(x / 2, y / 8, 2) > 0.3f;
            bool crack = Noise(x / 4, y / 2, 6) > 0.95f;
            float shade = groove ? 0.62f : crack ? 0.68f : fibre;
            return shade * (0.94f + 0.06f * Noise(x, y, 3));
        }

        // Leaves: leaves of three by three pixels, lighter or darker, with dark gaps between some and
        // a lighter edge on top of the bright ones.
        private static float LeafPixel(int x, int y)
        {
            float leaf = Noise(x / 3, y / 3, 4);
            if (leaf < 0.12f) return 0.6f;
            if (leaf > 0.86f) return y % 3 == 2 ? 1f : 0.95f;
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
