using System;
using System.Collections.Generic;
using UnityEngine;

namespace Voxel.EditorTools
{
    // The crops' models: cereals, vegetables, fruit bushes and fruit trees, two models each, built
    // by a few generators (a fruit tree, a palm, a field of stalks, a leafy crop…) from a seed, so
    // that the two models of a species differ. Each has its fruit apart (Model.Fruit), drawn once
    // the crop is ripe and larger at each stage of ripeness; oaks get acorns and pines cones.
    public static partial class PlantModelBuilder
    {
        private const string CropLeaves = "PlantCropLeaves";
        private const string DarkCropLeaves = "PlantCropLeavesDark";
        private const string FeatheryLeaves = "PlantCarrotLeaves";
        private const string Stalk = "PlantStalk";
        private const string WheatEar = "PlantWheatEar";
        private const string OatEar = "PlantOatEar";
        private const string RiceEar = "PlantRiceEar";
        private const string CornCob = "PlantCorn";
        private const string Husk = "PlantHusk";
        private const string SunPetal = "PlantSunflowerPetal";
        private const string SunCentre = "PlantSunflowerCentre";
        private const string CarrotRoot = "PlantCarrot";
        private const string BeetRoot = "PlantBeet";
        private const string BeetStem = "PlantBeetStem";
        private const string PotatoSkin = "PlantPotato";
        private const string CabbageHead = "PlantCabbage";
        private const string CabbageLeaves = "PlantCabbageLeaves";
        private const string Squash = "PlantButternut";
        private const string BeanPod = "PlantBean";
        private const string TomatoFruit = "PlantTomato";
        private const string StrawberryFruit = "PlantStrawberry";
        private const string GrapeFruit = "PlantGrape";
        private const string CottonBoll = "PlantCotton";
        private const string PearFruit = "PlantPricklyPear";
        private const string MushroomCap = "PlantMushroomCap";
        private const string MushroomStem = "PlantMushroomStem";
        private const string AppleFruit = "PlantApple";
        private const string CherryFruit = "PlantCherry";
        private const string LemonFruit = "PlantLemon";
        private const string OrangeFruit = "PlantOrange";
        private const string MangoFruit = "PlantMango";
        private const string BananaFruit = "PlantBanana";
        private const string BananaLeaves = "PlantBananaLeaves";
        private const string BananaTrunk = "PlantBananaTrunk";
        private const string CacaoPod = "PlantCacaoPod";
        private const string CoffeeLeaves = "PlantCoffeeLeaves";
        private const string CitrusLeaves = "PlantCitrusLeaves";
        private const string DateFruit = "PlantDate";
        private const string AcornFruit = "PlantAcorn";
        private const string PineCone = "PlantPineCone";
        private const string Post = "PlantPost";
        private const string VineLeaves = "PlantVineLeaves";
        private const string CottonLeaves = "PlantCottonLeaves";

        private static MaterialLook Look(string name, string texture, float r, float g, float b, float smoothness = 0.1f)
        {
            return new MaterialLook { Name = name, Texture = texture, Colour = new Color(r, g, b), Smoothness = smoothness };
        }

        private static readonly MaterialLook[] CropMaterials =
        {
            Look(CropLeaves, "Leaves", 0.3f, 0.6f, 0.22f),
            Look(DarkCropLeaves, "Leaves", 0.18f, 0.42f, 0.17f),
            Look(FeatheryLeaves, "Leaves", 0.38f, 0.68f, 0.25f),
            Look(Stalk, "Leaves", 0.45f, 0.62f, 0.28f),
            Look(WheatEar, "Leaves", 0.86f, 0.72f, 0.36f),
            Look(OatEar, "Leaves", 0.85f, 0.8f, 0.55f),
            Look(RiceEar, "Leaves", 0.8f, 0.76f, 0.45f),
            Look(CornCob, null, 0.96f, 0.8f, 0.25f, 0.3f),
            Look(Husk, "Leaves", 0.55f, 0.68f, 0.32f),
            Look(SunPetal, null, 0.98f, 0.78f, 0.12f, 0.2f),
            Look(SunCentre, "Bark", 0.3f, 0.2f, 0.1f),
            Look(CarrotRoot, null, 0.93f, 0.5f, 0.12f, 0.3f),
            Look(BeetRoot, null, 0.5f, 0.1f, 0.2f, 0.3f),
            Look(BeetStem, null, 0.65f, 0.15f, 0.25f, 0.2f),
            Look(PotatoSkin, "Bark", 0.72f, 0.58f, 0.38f),
            Look(CabbageHead, "Leaves", 0.72f, 0.85f, 0.55f, 0.2f),
            Look(CabbageLeaves, "Leaves", 0.45f, 0.66f, 0.42f, 0.2f),
            Look(Squash, null, 0.88f, 0.7f, 0.42f, 0.3f),
            Look(BeanPod, null, 0.4f, 0.62f, 0.22f, 0.2f),
            Look(TomatoFruit, null, 0.85f, 0.15f, 0.1f, 0.5f),
            Look(StrawberryFruit, null, 0.86f, 0.1f, 0.15f, 0.5f),
            Look(GrapeFruit, null, 0.35f, 0.15f, 0.4f, 0.4f),
            Look(CottonBoll, null, 0.97f, 0.96f, 0.92f),
            Look(PearFruit, null, 0.78f, 0.18f, 0.42f, 0.3f),
            Look(MushroomCap, null, 0.68f, 0.36f, 0.2f, 0.2f),
            Look(MushroomStem, null, 0.92f, 0.88f, 0.78f),
            Look(AppleFruit, null, 0.82f, 0.12f, 0.1f, 0.45f),
            Look(CherryFruit, null, 0.6f, 0.05f, 0.1f, 0.5f),
            Look(LemonFruit, null, 0.96f, 0.88f, 0.2f, 0.4f),
            Look(OrangeFruit, null, 0.96f, 0.55f, 0.08f, 0.4f),
            Look(MangoFruit, null, 0.95f, 0.6f, 0.15f, 0.4f),
            Look(BananaFruit, null, 0.95f, 0.85f, 0.25f, 0.3f),
            Look(BananaLeaves, "Leaves", 0.36f, 0.62f, 0.22f, 0.15f),
            Look(BananaTrunk, "Bark", 0.5f, 0.55f, 0.3f),
            Look(CacaoPod, null, 0.8f, 0.45f, 0.15f, 0.3f),
            Look(CoffeeLeaves, "Leaves", 0.12f, 0.38f, 0.15f, 0.3f),
            Look(CitrusLeaves, "Leaves", 0.15f, 0.42f, 0.16f, 0.25f),
            Look(DateFruit, null, 0.55f, 0.3f, 0.12f, 0.3f),
            Look(AcornFruit, null, 0.52f, 0.36f, 0.16f, 0.2f),
            Look(PineCone, "Bark", 0.45f, 0.3f, 0.17f),
            Look(Post, "Bark", 0.5f, 0.38f, 0.24f),
            Look(VineLeaves, "Leaves", 0.32f, 0.55f, 0.2f),
            Look(CottonLeaves, "Leaves", 0.25f, 0.45f, 0.22f),
        };

        // Each crop species and its models.
        private static (string Species, Model[] Models)[] CropModels()
        {
            return new[]
            {
                ("Oat", new[] { Grain("Oat", 1, OatEar, 0.95f, 9, new Vector3(0.12f, 0.2f, 0.12f), 18f), Grain("OatSparse", 2, OatEar, 1.05f, 7, new Vector3(0.12f, 0.22f, 0.12f), 22f) }),
                ("Wheat", new[] { Grain("Wheat", 3, WheatEar, 1.0f, 10, new Vector3(0.09f, 0.22f, 0.09f), 4f), Grain("WheatDense", 4, WheatEar, 0.95f, 13, new Vector3(0.09f, 0.2f, 0.09f), 6f) }),
                ("Beet", new[] { Beet("Beet", 5), Beet("BeetWide", 6) }),
                ("Carrot", new[] { Carrot("Carrot", 7), Carrot("CarrotTall", 8) }),
                ("Potato", new[] { Potato("Potato", 9), Potato("PotatoWide", 10) }),
                ("Cabbage", new[] { Cabbage("Cabbage", 11, 0f), Cabbage("CabbageTurned", 12, 0.12f) }),
                ("Maize", new[] { Maize("Maize", 13, 2), Maize("MaizeTall", 14, 3) }),
                ("Sunflower", new[] { Sunflower("Sunflower", 15, 2.0f), Sunflower("SunflowerShort", 16, 1.6f) }),
                ("Bean", new[] { Bean("Bean", 17), Bean("BeanTall", 18) }),
                ("Butternut", new[] { Butternut("Butternut", 19), Butternut("ButternutWide", 20) }),
                ("Rice", new[] { Grain("Rice", 21, RiceEar, 1.15f, 11, new Vector3(0.08f, 0.24f, 0.08f), 35f), Grain("RiceDense", 22, RiceEar, 1.1f, 14, new Vector3(0.08f, 0.22f, 0.08f), 40f) }),
                ("Mushroom", new[] { Mushroom("Mushroom", 23), Mushroom("Mushrooms", 24) }),
                ("Strawberry", new[] { Strawberry("Strawberry", 25), Strawberry("StrawberryWide", 26) }),
                ("Tomato", new[] { Tomato("Tomato", 27), Tomato("TomatoTall", 28) }),
                ("Grapevine", new[] { Grapevine("Grapevine", 29), Grapevine("GrapevineLong", 30) }),
                ("Cotton", new[] { Cotton("Cotton", 31), Cotton("CottonTall", 32) }),
                ("PricklyPear", new[] { PricklyPear("PricklyPear", 33), PricklyPear("PricklyPearTall", 34) }),
                ("AppleTree", new[]
                {
                    FruitTree("AppleTree", 35, Bark, Leaves, AppleFruit, new Vector3(0.21f, 0.21f, 0.21f), 72, 1.9f, 3.0f, 2.0f),
                    FruitTree("AppleTreeBroad", 36, Bark, LightLeaves, AppleFruit, new Vector3(0.21f, 0.2f, 0.21f), 76, 1.6f, 3.4f, 1.7f),
                }),
                ("CherryTree", new[]
                {
                    FruitTree("CherryTree", 37, GreyBark, Leaves, CherryFruit, new Vector3(0.12f, 0.14f, 0.12f), 96, 2.2f, 2.8f, 2.2f),
                    FruitTree("CherryTreeTall", 38, GreyBark, LightLeaves, CherryFruit, new Vector3(0.12f, 0.14f, 0.12f), 90, 2.6f, 2.5f, 2.4f),
                }),
                ("LemonTree", new[]
                {
                    FruitTree("LemonTree", 39, Bark, CitrusLeaves, LemonFruit, new Vector3(0.17f, 0.23f, 0.17f), 56, 1.2f, 2.4f, 1.8f),
                    FruitTree("LemonTreeRound", 40, Bark, CitrusLeaves, LemonFruit, new Vector3(0.17f, 0.23f, 0.17f), 54, 1.0f, 2.2f, 2.0f),
                }),
                ("OrangeTree", new[]
                {
                    FruitTree("OrangeTree", 41, Bark, CitrusLeaves, OrangeFruit, new Vector3(0.22f, 0.22f, 0.22f), 66, 1.4f, 2.8f, 2.2f),
                    FruitTree("OrangeTreeRound", 42, Bark, CitrusLeaves, OrangeFruit, new Vector3(0.22f, 0.22f, 0.22f), 70, 1.2f, 3.0f, 2.4f),
                }),
                ("BananaTree", new[] { BananaTree("BananaTree", 43, 2.4f), BananaTree("BananaTreeTall", 44, 3.0f) }),
                ("CacaoTree", new[] { CacaoTree("CacaoTree", 45, 2.4f), CacaoTree("CacaoTreeTall", 46, 2.9f) }),
                ("CoffeeShrub", new[]
                {
                    FruitTree("CoffeeShrub", 47, Bark, CoffeeLeaves, Berries, new Vector3(0.09f, 0.09f, 0.09f), 64, 0.5f, 1.3f, 1.4f),
                    FruitTree("CoffeeShrubTall", 48, Bark, CoffeeLeaves, Berries, new Vector3(0.09f, 0.09f, 0.09f), 60, 0.7f, 1.1f, 1.7f),
                }),
                ("MangoTree", new[]
                {
                    FruitTree("MangoTree", 49, Bark, JungleLeaves, MangoFruit, new Vector3(0.2f, 0.28f, 0.2f), 70, 3.0f, 4.0f, 2.6f),
                    FruitTree("MangoTreeTall", 50, Bark, JungleLeaves, MangoFruit, new Vector3(0.2f, 0.28f, 0.2f), 74, 3.6f, 3.6f, 3.0f),
                }),
                ("DatePalm", new[] { DatePalm("DatePalm", 51, 4.6f), DatePalm("DatePalmTall", 52, 5.6f) }),
            };
        }

        // Acorns under the oaks' crowns, cones on the pines' tiers.
        private static void GiveTreesFruit()
        {
            foreach (Model oak in Oaks)
            {
                oak.Fruit = new[] { P(AcornFruit, FruitOn(BoxesOf(oak, Leaves), 48, new Vector3(0.11f, 0.14f, 0.11f), 61, FruitFaces.Sides | FruitFaces.Under)) };
            }

            foreach (Model pine in Pines)
            {
                pine.Fruit = new[] { P(PineCone, FruitOn(BoxesOf(pine, Needles), 32, new Vector3(0.14f, 0.22f, 0.14f), 67, FruitFaces.Sides | FruitFaces.Under)) };
            }
        }

        // ---- Generators ----

        // A fruit tree: a trunk forking into a crown of a few boxes, the fruit hanging on its sides and underside.
        private static Model FruitTree(string name, int seed, string bark, string leaves, string fruit, Vector3 fruitSize, int fruitCount,
            float trunkHeight, float crownWidth, float crownHeight)
        {
            float top = trunkHeight;
            float w = crownWidth;
            float h = crownHeight;
            float lean = (N(seed, 0, 0) - 0.5f) * 0.2f;
            Box[] trunk =
            {
                new Box(0f, 0.12f, 0f, 0.5f, 0.24f, 0.5f),
                new Box(0f, top * 0.55f, 0f, 0.32f, top * 1.1f, 0.32f),
                new Box(0.28f, top * 0.95f + 0.1f, 0.04f, 0.17f, 0.9f, 0.17f, tiltZ: -38f),
                new Box(-0.22f, top + 0.05f, -0.12f, 0.15f, 0.8f, 0.15f, tiltX: -30f, tiltZ: 30f),
            };
            Box[] crown =
            {
                new Box(lean, top + h * 0.5f, 0f, w, h, w * 0.94f),
                new Box(lean + 0.08f, top + h * 1.02f, -0.06f, w * 0.62f, h * 0.42f, w * 0.58f),
                new Box(lean + w * 0.43f, top + h * 0.36f, 0.12f, w * 0.42f, h * 0.62f, w * 0.5f),
                new Box(lean - w * 0.39f, top + h * 0.43f, -0.17f, w * 0.4f, h * 0.58f, w * 0.46f),
            };

            return new Model
            {
                Name = name,
                Parts = new[] { P(bark, trunk), P(leaves, crown) },
                Fruit = new[] { P(fruit, FruitOn(crown, fruitCount, fruitSize, seed, FruitFaces.Sides | FruitFaces.Under)) }
            };
        }

        // Cacao: a small tree of the jungle's shade whose pods grow straight out of the trunk.
        private static Model CacaoTree(string name, int seed, float trunkHeight)
        {
            Model tree = FruitTree(name, seed, Bark, JungleLeaves, CacaoPod, Vector3.one, 1, trunkHeight, 2.4f, 1.6f);
            var trunk = new Box(0f, trunkHeight * 0.4f, 0f, 0.3f, trunkHeight * 0.7f, 0.3f);
            tree.Fruit = new[] { P(CacaoPod, FruitOn(new[] { trunk }, 7, new Vector3(0.14f, 0.26f, 0.14f), seed, FruitFaces.Sides)) };
            return tree;
        }

        // Banana: a soft green stem, long leaves arching out from its top, and a bunch hanging below them.
        private static Model BananaTree(string name, int seed, float height)
        {
            float top = height;
            var leaves = new List<Box>();
            for (int i = 0; i < 5; i++)
            {
                float angle = (i * 72f + N(seed, i, 0) * 30f) * Mathf.Deg2Rad;
                float reach = 0.85f + 0.2f * N(seed, i, 1);
                float x = Mathf.Cos(angle) * reach;
                float z = Mathf.Sin(angle) * reach;
                // Each leaf is long along its own way out: across x if it points east or west, else across z.
                bool alongX = Mathf.Abs(x) > Mathf.Abs(z);
                leaves.Add(new Box(x, top + 0.15f + 0.05f * i, z, alongX ? 1.8f : 0.5f, 0.07f, alongX ? 0.5f : 1.8f,
                    tiltX: alongX ? 0f : (z > 0f ? 22f : -22f), tiltZ: alongX ? (x > 0f ? -22f : 22f) : 0f));
            }

            leaves.Add(new Box(0.05f, top + 0.7f, 0.02f, 0.4f, 1.1f, 0.08f, tiltX: -12f));
            Box[] stem = { new Box(0f, top * 0.5f, 0f, 0.38f, top, 0.38f), new Box(0f, 0.15f, 0f, 0.52f, 0.3f, 0.52f) };
            Box[] bunch =
            {
                new Box(0.34f, top - 0.45f, 0.1f, 0.34f, 0.55f, 0.34f),
                new Box(0.36f, top - 0.18f, 0.12f, 0.26f, 0.2f, 0.42f),
                new Box(0.32f, top - 0.8f, 0.08f, 0.22f, 0.25f, 0.24f),
            };

            return new Model { Name = name, Parts = new[] { P(BananaTrunk, stem), P(BananaLeaves, leaves) }, Fruit = new[] { P(BananaFruit, bunch) } };
        }

        // Date palm: a tall ringed trunk, fronds drooping all around, clusters of dates hanging under them.
        private static Model DatePalm(string name, int seed, float height)
        {
            var fronds = new List<Box>();
            for (int i = 0; i < 6; i++)
            {
                float angle = (i * 60f + N(seed, i, 0) * 20f) * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle) * 1.0f;
                float z = Mathf.Sin(angle) * 1.0f;
                bool alongX = Mathf.Abs(x) > Mathf.Abs(z);
                fronds.Add(new Box(x, height + 0.05f * i - 0.1f, z, alongX ? 2.0f : 0.45f, 0.08f, alongX ? 0.45f : 2.0f,
                    tiltX: alongX ? 0f : (z > 0f ? 28f : -28f), tiltZ: alongX ? (x > 0f ? -28f : 28f) : 0f));
            }

            fronds.Add(new Box(0f, height + 0.55f, 0f, 0.3f, 0.9f, 0.3f));
            Box[] trunk =
            {
                new Box(0f, 0.15f, 0f, 0.6f, 0.3f, 0.6f),
                new Box(0f, height * 0.5f, 0f, 0.42f, height, 0.42f),
                new Box(0f, height * 0.25f, 0f, 0.48f, 0.12f, 0.48f),
                new Box(0f, height * 0.55f, 0f, 0.47f, 0.12f, 0.47f),
            };
            Box[] dates =
            {
                new Box(0.36f, height - 0.35f, 0.05f, 0.26f, 0.5f, 0.26f),
                new Box(-0.3f, height - 0.3f, -0.2f, 0.24f, 0.45f, 0.24f),
                new Box(0.05f, height - 0.4f, 0.37f, 0.25f, 0.48f, 0.25f),
            };

            return new Model { Name = name, Parts = new[] { P(PalmTrunk, trunk), P(Frond, fronds) }, Fruit = new[] { P(DateFruit, dates) } };
        }

        // A clump of cereal stalks, each topped by its ear (the fruit), nodding by `droop` degrees.
        private static Model Grain(string name, int seed, string ear, float height, int stalks, Vector3 earSize, float droop)
        {
            var stems = new List<Box>();
            var ears = new List<Box>();
            for (int i = 0; i < stalks; i++)
            {
                float x = (N(seed, i, 0) - 0.5f) * 0.7f;
                float z = (N(seed, i, 1) - 0.5f) * 0.7f;
                float h = height * (0.8f + 0.25f * N(seed, i, 2));
                float tiltX = (N(seed, i, 3) - 0.5f) * 14f;
                float tiltZ = (N(seed, i, 4) - 0.5f) * 14f;
                var tilt = Quaternion.Euler(tiltX, 0f, tiltZ);
                stems.Add(new Box(x, h * 0.5f, z, 0.045f, h, 0.045f, tiltX, tiltZ));

                Vector3 top = new Vector3(x, h * 0.5f, z) + tilt * new Vector3(0f, h * 0.5f + earSize.y * 0.4f, 0f);
                float nod = droop * (N(seed, i, 5) > 0.5f ? 1f : -1f);
                ears.Add(new Box(top.x, top.y, top.z, earSize.x, earSize.y, earSize.z, tiltX + nod, tiltZ));
            }

            // A few low blades among the stalks.
            for (int i = 0; i < 3; i++)
            {
                float x = (N(seed, i, 6) - 0.5f) * 0.5f;
                float z = (N(seed, i, 7) - 0.5f) * 0.5f;
                stems.Add(new Box(x, 0.2f, z, 0.06f, 0.4f, 0.14f, (N(seed, i, 8) - 0.5f) * 50f, (N(seed, i, 9) - 0.5f) * 50f));
            }

            return new Model { Name = name, Parts = new[] { P(Stalk, stems) }, Fruit = new[] { P(ear, ears) } };
        }

        // Maize: tall stalks with long arching leaves and a tassel, cobs growing from their sides.
        private static Model Maize(string name, int seed, int stalks)
        {
            var stems = new List<Box>();
            var leaves = new List<Box>();
            var tassels = new List<Box>();
            var cobs = new List<Box>();
            for (int i = 0; i < stalks; i++)
            {
                float x = (i - (stalks - 1) * 0.5f) * 0.28f + (N(seed, i, 0) - 0.5f) * 0.1f;
                float z = (N(seed, i, 1) - 0.5f) * 0.3f;
                float h = 2.0f + 0.4f * N(seed, i, 2);
                stems.Add(new Box(x, h * 0.5f, z, 0.1f, h, 0.1f));
                tassels.Add(new Box(x, h + 0.15f, z, 0.05f, 0.3f, 0.05f));
                for (int l = 0; l < 3; l++)
                {
                    float y = 0.6f + l * 0.5f + 0.1f * N(seed, i, 3 + l);
                    float side = (l + i) % 2 == 0 ? 1f : -1f;
                    leaves.Add(new Box(x + side * 0.32f, y, z, 0.6f, 0.04f, 0.13f, tiltZ: -side * 35f));
                }

                float cobSide = i % 2 == 0 ? 1f : -1f;
                cobs.Add(new Box(x + cobSide * 0.11f, 1.05f + 0.1f * N(seed, i, 7), z + 0.03f, 0.12f, 0.34f, 0.12f, tiltZ: -cobSide * 18f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(Stalk, stems), P(Husk, leaves), P(Straw, tassels) },
                Fruit = new[] { P(CornCob, cobs) }
            };
        }

        // Sunflower: a tall stalk with broad leaves and a green head facing out; ripe, its petals and seeds show.
        private static Model Sunflower(string name, int seed, float height)
        {
            Box[] stem = { new Box(0f, height * 0.5f, 0f, 0.09f, height, 0.09f) };
            var leaves = new List<Box>();
            for (int l = 0; l < 4; l++)
            {
                float y = 0.5f + l * height * 0.18f;
                float side = l % 2 == 0 ? 1f : -1f;
                leaves.Add(new Box(side * 0.22f, y, (N(seed, l, 0) - 0.5f) * 0.1f, 0.38f, 0.04f, 0.3f, tiltZ: -side * 25f));
            }

            Box[] back = { new Box(0f, height + 0.05f, 0.06f, 0.5f, 0.5f, 0.08f, tiltX: -20f) };
            Box[] petals = { new Box(0f, height + 0.05f, 0.125f, 0.74f, 0.74f, 0.04f, tiltX: -20f) };
            Box[] centre = { new Box(0f, height + 0.05f, 0.16f, 0.4f, 0.4f, 0.05f, tiltX: -20f) };

            return new Model
            {
                Name = name,
                Parts = new[] { P(Stalk, stem), P(CropLeaves, leaves), P(Husk, back) },
                Fruit = new[] { P(SunPetal, petals), P(SunCentre, centre) }
            };
        }

        // Carrot: a tuft of feathery leaves; ripe, the orange top of the root shows at the ground.
        private static Model Carrot(string name, int seed)
        {
            var leaves = new List<Box>();
            for (int i = 0; i < 7; i++)
            {
                float angle = (i * 51f + N(seed, i, 0) * 20f) * Mathf.Deg2Rad;
                float h = 0.38f + 0.18f * N(seed, i, 1);
                leaves.Add(new Box(Mathf.Cos(angle) * 0.07f, h * 0.5f + 0.04f, Mathf.Sin(angle) * 0.07f, 0.05f, h, 0.12f,
                    tiltX: Mathf.Sin(angle) * 25f, tiltZ: -Mathf.Cos(angle) * 25f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(FeatheryLeaves, leaves) },
                Fruit = new[] { P(CarrotRoot, new[] { new Box(0.01f, 0.045f, -0.01f, 0.17f, 0.11f, 0.17f) }) }
            };
        }

        // Beet: broad dark leaves on red stems; ripe, the round red root shows at the ground.
        private static Model Beet(string name, int seed)
        {
            var leaves = new List<Box>();
            var stems = new List<Box>();
            for (int i = 0; i < 5; i++)
            {
                float angle = (i * 72f + N(seed, i, 0) * 25f) * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle);
                float z = Mathf.Sin(angle);
                stems.Add(new Box(x * 0.06f, 0.14f, z * 0.06f, 0.035f, 0.26f, 0.035f, tiltX: z * 20f, tiltZ: -x * 20f));
                leaves.Add(new Box(x * 0.2f, 0.33f + 0.03f * i, z * 0.2f, 0.2f, 0.03f + 0.002f * i, 0.32f, tiltX: z * 35f, tiltZ: -x * 35f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(DarkCropLeaves, leaves), P(BeetStem, stems) },
                Fruit = new[] { P(BeetRoot, new[] { new Box(0f, 0.08f, 0f, 0.26f, 0.18f, 0.26f) }) }
            };
        }

        // Potato: a low mound of leaves; ripe, a few potatoes show around its foot.
        private static Model Potato(string name, int seed)
        {
            Box[] leaves =
            {
                new Box(0f, 0.25f, 0f, 0.72f, 0.36f, 0.62f),
                new Box(0.12f, 0.48f, -0.05f, 0.44f, 0.22f, 0.4f),
                new Box(-0.28f, 0.2f, 0.18f, 0.32f, 0.26f, 0.34f),
            };
            var potatoes = new List<Box>();
            for (int i = 0; i < 4; i++)
            {
                float angle = (i * 90f + 45f + N(seed, i, 0) * 30f) * Mathf.Deg2Rad;
                potatoes.Add(new Box(Mathf.Cos(angle) * 0.38f, 0.05f, Mathf.Sin(angle) * 0.38f, 0.16f, 0.11f, 0.13f));
            }

            return new Model { Name = name, Parts = new[] { P(CropLeaves, leaves) }, Fruit = new[] { P(PotatoSkin, potatoes) } };
        }

        // Cabbage: outer leaves spread flat; the head in their middle swells as it ripens.
        private static Model Cabbage(string name, int seed, float offset)
        {
            var leaves = new List<Box>();
            for (int i = 0; i < 5; i++)
            {
                float angle = (i * 72f + N(seed, i, 0) * 20f) * Mathf.Deg2Rad;
                float x = Mathf.Cos(angle);
                float z = Mathf.Sin(angle);
                leaves.Add(new Box(x * 0.25f, 0.1f + 0.012f * i, z * 0.25f, 0.34f, 0.04f, 0.34f, tiltX: z * 25f, tiltZ: -x * 25f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(CabbageLeaves, leaves) },
                Fruit = new[] { P(CabbageHead, new[] { new Box(offset, 0.24f, -offset * 0.5f, 0.44f, 0.38f, 0.42f) }) }
            };
        }

        // Butternut: a vine of broad leaves lying low; ripe, its long tan squashes lie on the ground.
        private static Model Butternut(string name, int seed)
        {
            var leaves = new List<Box>();
            for (int i = 0; i < 5; i++)
            {
                float x = (N(seed, i, 0) - 0.5f) * 0.8f;
                float z = (N(seed, i, 1) - 0.5f) * 0.8f;
                leaves.Add(new Box(x, 0.12f + 0.04f * i, z, 0.32f, 0.04f, 0.3f, tiltX: (N(seed, i, 2) - 0.5f) * 30f));
            }

            Box[] squashes =
            {
                new Box(0.3f, 0.1f, 0.22f, 0.42f, 0.18f, 0.2f),
                new Box(-0.25f, 0.095f, -0.3f, 0.19f, 0.17f, 0.4f),
            };

            return new Model { Name = name, Parts = new[] { P(CropLeaves, leaves) }, Fruit = new[] { P(Squash, squashes) } };
        }

        // Bean: a pole climbed by leafy stems; ripe, green pods hang from them.
        private static Model Bean(string name, int seed)
        {
            float height = 1.5f + 0.4f * N(seed, 0, 0);
            Box[] pole = { new Box(0f, height * 0.5f, 0f, 0.06f, height, 0.06f) };
            var clumps = new List<Box>();
            for (int i = 0; i < 4; i++)
            {
                float y = 0.3f + i * height * 0.22f;
                float side = i % 2 == 0 ? 1f : -1f;
                clumps.Add(new Box(side * 0.1f, y, (N(seed, i, 1) - 0.5f) * 0.15f, 0.3f, 0.26f, 0.28f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(Post, pole), P(CropLeaves, clumps) },
                Fruit = new[] { P(BeanPod, FruitOn(clumps.ToArray(), 9, new Vector3(0.05f, 0.24f, 0.05f), seed, FruitFaces.Sides | FruitFaces.Under)) }
            };
        }

        // Tomato: a staked leafy plant; its tomatoes redden among the leaves.
        private static Model Tomato(string name, int seed)
        {
            float height = 1.1f + 0.3f * N(seed, 0, 0);
            Box[] stake = { new Box(0.18f, height * 0.5f, 0.05f, 0.05f, height, 0.05f) };
            Box[] leaves =
            {
                new Box(0f, height * 0.3f, 0f, 0.6f, height * 0.42f, 0.55f),
                new Box(0.04f, height * 0.68f, -0.03f, 0.48f, height * 0.36f, 0.44f),
                new Box(-0.02f, height * 0.95f, 0.02f, 0.3f, height * 0.2f, 0.3f),
            };

            return new Model
            {
                Name = name,
                Parts = new[] { P(Post, stake), P(CropLeaves, leaves) },
                Fruit = new[] { P(TomatoFruit, FruitOn(leaves, 10, new Vector3(0.13f, 0.12f, 0.13f), seed, FruitFaces.Sides)) }
            };
        }

        // Strawberry: low tufts of dark leaves; its berries hang at their edges.
        private static Model Strawberry(string name, int seed)
        {
            var tufts = new List<Box>();
            for (int i = 0; i < 3; i++)
            {
                float x = (N(seed, i, 0) - 0.5f) * 0.5f;
                float z = (N(seed, i, 1) - 0.5f) * 0.5f;
                tufts.Add(new Box(x, 0.12f + 0.01f * i, z, 0.3f + 0.05f * i, 0.22f, 0.28f + 0.04f * i));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(DarkCropLeaves, tufts) },
                Fruit = new[] { P(StrawberryFruit, FruitOn(tufts.ToArray(), 8, new Vector3(0.08f, 0.1f, 0.08f), seed, FruitFaces.Sides)) }
            };
        }

        // Grapevine: a post and a cross bar carrying the vine; bunches of grapes hang under its leaves.
        private static Model Grapevine(string name, int seed)
        {
            float width = 1.1f + 0.4f * N(seed, 0, 0);
            Box[] posts = { new Box(0f, 0.8f, 0f, 0.1f, 1.6f, 0.1f), new Box(0f, 1.52f, 0f, width, 0.07f, 0.07f) };
            Box[] vine = { new Box(0.06f, 0.7f, 0.06f, 0.08f, 1.4f, 0.08f, tiltZ: -6f) };
            var leaves = new List<Box>();
            for (int i = 0; i < 3; i++)
            {
                float x = (i - 1) * width * 0.34f;
                leaves.Add(new Box(x, 1.6f + 0.03f * i, 0.02f, width * 0.36f, 0.24f, 0.4f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(Post, posts), P(Bark, vine), P(VineLeaves, leaves) },
                Fruit = new[] { P(GrapeFruit, FruitOn(leaves.ToArray(), 5, new Vector3(0.16f, 0.26f, 0.16f), seed, FruitFaces.Under)) }
            };
        }

        // Cotton: a branching shrub; ripe, its white bolls burst open among the leaves.
        private static Model Cotton(string name, int seed)
        {
            float height = 0.9f + 0.3f * N(seed, 0, 0);
            Box[] stems =
            {
                new Box(0f, height * 0.4f, 0f, 0.07f, height * 0.8f, 0.07f),
                new Box(0.15f, height * 0.6f, 0f, 0.05f, height * 0.5f, 0.05f, tiltZ: -30f),
                new Box(-0.12f, height * 0.55f, 0.05f, 0.05f, height * 0.45f, 0.05f, tiltZ: 30f),
            };
            Box[] leaves =
            {
                new Box(0f, height * 0.75f, 0f, 0.6f, height * 0.35f, 0.56f),
                new Box(0.05f, height, -0.03f, 0.36f, height * 0.25f, 0.34f),
            };

            return new Model
            {
                Name = name,
                Parts = new[] { P(Bark, stems), P(CottonLeaves, leaves) },
                Fruit = new[] { P(CottonBoll, FruitOn(leaves, 12, new Vector3(0.14f, 0.12f, 0.14f), seed, FruitFaces.Sides | FruitFaces.Top)) }
            };
        }

        // Prickly pear: flat pads stacked on one another; its fruit grow on their top edges.
        private static Model PricklyPear(string name, int seed)
        {
            float height = 0.55f + 0.2f * N(seed, 0, 0);
            Box[] pads =
            {
                new Box(0f, height * 0.5f, 0f, 0.5f, height, 0.12f),
                new Box(0.3f, height + 0.25f, 0.03f, 0.4f, 0.5f, 0.11f),
                new Box(-0.27f, height + 0.2f, -0.04f, 0.38f, 0.46f, 0.1f),
                new Box(0.02f, height * 0.45f, 0.24f, 0.12f, height * 0.85f, 0.42f),
            };

            return new Model
            {
                Name = name,
                Parts = new[] { P(CactusSkin, pads) },
                Fruit = new[] { P(PearFruit, FruitOn(pads, 7, new Vector3(0.1f, 0.14f, 0.1f), seed, FruitFaces.Top)) }
            };
        }

        // Mushroom: one mushroom on its stem; ripe, more of them come up around it.
        private static Model Mushroom(string name, int seed)
        {
            Box[] stem = { new Box(0f, 0.07f, 0f, 0.07f, 0.14f, 0.07f) };
            Box[] cap = { new Box(0f, 0.15f, 0f, 0.26f, 0.07f, 0.26f), new Box(0f, 0.2f, 0f, 0.16f, 0.05f, 0.16f) };
            var stems = new List<Box>();
            var caps = new List<Box>();
            for (int i = 0; i < 4; i++)
            {
                float angle = (i * 90f + N(seed, i, 0) * 50f) * Mathf.Deg2Rad;
                float r = 0.22f + 0.1f * N(seed, i, 1);
                float x = Mathf.Cos(angle) * r;
                float z = Mathf.Sin(angle) * r;
                float h = 0.08f + 0.05f * N(seed, i, 2);
                stems.Add(new Box(x, h * 0.5f, z, 0.05f, h, 0.05f));
                caps.Add(new Box(x, h - 0.005f, z, 0.17f, 0.05f, 0.17f));
            }

            return new Model
            {
                Name = name,
                Parts = new[] { P(MushroomStem, stem), P(MushroomCap, cap) },
                Fruit = new[] { P(MushroomStem, stems), P(MushroomCap, caps) }
            };
        }

        // ---- Tools ----

        [Flags]
        private enum FruitFaces
        {
            Sides = 1,
            Under = 2,
            Top = 4
        }

        // Fruit on the outside of some boxes (untilted): count boxes of the given size, each on one
        // face of a host (the given faces in turn, at a place picked from the seed), sticking out of
        // it by seven tenths of its size, so that no fruit face lies in the plane of its host's.
        private static Box[] FruitOn(Box[] hosts, int count, Vector3 size, int seed, FruitFaces faces)
        {
            var choices = new List<int>();
            if ((faces & FruitFaces.Sides) != 0) choices.AddRange(new[] { 0, 1, 2, 3 });
            if ((faces & FruitFaces.Under) != 0) choices.Add(4);
            if ((faces & FruitFaces.Top) != 0) choices.Add(5);

            // Hosts are picked by their surface, so that a crown's main box carries most of its fruit.
            var areas = new float[hosts.Length];
            float totalArea = 0f;
            for (int h = 0; h < hosts.Length; h++)
            {
                Vector3 s = hosts[h].Size;
                totalArea += s.x * s.y + s.y * s.z + s.x * s.z;
                areas[h] = totalArea;
            }

            var fruit = new Box[count];
            for (int i = 0; i < count; i++)
            {
                float pick = N(seed, i, 10) * totalArea;
                int index = 0;
                while (index < hosts.Length - 1 && areas[index] < pick) index++;
                Box host = hosts[index];
                // Faces in turn, so that every side gets its share.
                int face = choices[(i + seed) % choices.Count];
                Vector3 half = host.Size * 0.5f;
                float u = (N(seed, i, 12) - 0.5f) * 1.6f;
                float v = (N(seed, i, 13) - 0.5f) * 1.6f;
                Vector3 offset;
                switch (face)
                {
                    case 0: offset = new Vector3(half.x + size.x * 0.2f, v * half.y, u * half.z); break;
                    case 1: offset = new Vector3(-half.x - size.x * 0.2f, v * half.y, u * half.z); break;
                    case 2: offset = new Vector3(u * half.x, v * half.y, half.z + size.z * 0.2f); break;
                    case 3: offset = new Vector3(u * half.x, v * half.y, -half.z - size.z * 0.2f); break;
                    case 4: offset = new Vector3(u * half.x, -half.y - size.y * 0.2f, v * half.z); break;
                    default: offset = new Vector3(u * half.x, half.y + size.y * 0.2f, v * half.z); break;
                }

                Vector3 centre = host.Centre + offset;
                fruit[i] = new Box(centre.x, centre.y, centre.z, size.x, size.y, size.z);
            }

            return fruit;
        }

        private static Box[] BoxesOf(Model model, string material)
        {
            var boxes = new List<Box>();
            foreach (Part part in model.Parts)
            {
                if (part.Material == material) boxes.AddRange(part.Boxes);
            }

            return boxes.ToArray();
        }

        private static Part P(string material, IEnumerable<Box> boxes) => new Part { Material = material, Boxes = new List<Box>(boxes).ToArray() };

        private static float N(int seed, int i, int k) => Noise(i, k, seed);
    }
}
