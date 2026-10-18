using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Voxel.Client;

namespace Voxel.EditorTools
{
    // Builds the models of the animal species from boxes, in the blocky style of the world: a bird
    // (BirdLayout), a fish (FishLayout), or, for most, a quadruped laid out from a few proportions
    // (legs, body, neck, head, snout, ears, tail) plus the features that set a species apart (antlers, horns, a ruff, a hump, a flank stripe, a dark
    // saddle, the big haunches and long feet of a hopping animal). It writes each species' parts,
    // their rig (which parts swing as legs, ears or tail, and about which point) and its gait into
    // its AnimalSpeciesAsset, and one material per colour (Animal<Species>_<Role>, URP Lit,
    // instancing on, as AnimalViews needs).
    //
    // The rabbit was modelled by hand, and the fox and the deer come from sculpted models
    // (ModelAnimalBuilder): they are not in this table. Run Eco > Rebuild
    // animal models after changing anything below; a species asset that does not exist yet is
    // created with default rules, to be set in the inspector. The animal faces +z, its feet at y = 0.
    public static class AnimalModelBuilder
    {
        private const string SpeciesFolder = "Assets/Species";
        private const string MaterialFolder = "Assets/Materials";

        [Flags]
        private enum Feature
        {
            None = 0,
            Antlers = 1,
            Horns = 2,
            Ruff = 4,
            Hump = 8,
            FlankStripe = 16,
            Saddle = 32,
            Haunches = 64,
            TailTip = 128,
            DarkTailTip = 256
        }

        private sealed class Spec
        {
            public string Name;
            public BodyShape Shape = BodyShape.Quadruped;
            public AnimalSpeciesAsset.Gait Gait = AnimalSpeciesAsset.Gait.Trot;
            public float WalkSpeed = 2.5f;
            public float Stride = 0.8f;
            public float HopLength = 0.6f;
            public float HopDuration = 0.3f;
            public float HopHeight = 0.2f;

            // Height of the body's underside, and the legs' thickness.
            public float LegHeight;
            public float Leg;

            // Width, height and length of the body.
            public Vector3 Body;

            // The neck rises and reaches forward from the front of the body.
            public float NeckRise;
            public float NeckForward;
            public float Neck;

            // Sizes (width, height, length); for ears, width, height and thickness.
            public Vector3 Head;
            public Vector3 Snout;
            public Vector3 Ear;

            public float TailLength;
            public float Tail;
            public bool TailHangs;

            public float AntlerHeight;
            public float AntlerSpread;
            public float HornHeight;

            public Feature Features;

            public Color Fur;
            public Color Light;
            public Color Dark;
            public Color Accent;

            // Birds: the beak (width, height, length) and the colour of beak and legs. Fish: the height of the body's centre.
            public Vector3 Beak;
            public Color Bill;
            public float SwimHeight;

            // Flying birds: the length of a wing, how high a flight goes and how fast the wings beat.
            public float WingSpan;
            public float FlightHeight = 4f;
            public float Wingbeats = 4f;
        }

        private const string FurRole = "Fur";
        private const string LightRole = "Light";
        private const string DarkRole = "Dark";
        private const string AccentRole = "Accent";
        private const string BillRole = "Bill";

        // The layouts: four legs; a bird on two legs, with wings, a beak and a short tail; a fish.
        private enum BodyShape
        {
            Quadruped,
            Bird,
            Fish
        }

        private static readonly Spec[] Species =
        {
            new Spec
            {
                Name = "Reindeer", WalkSpeed = 2.6f, Stride = 1.0f,
                LegHeight = 0.6f, Leg = 0.1f, Body = new Vector3(0.42f, 0.44f, 0.95f),
                NeckRise = 0.25f, NeckForward = 0.12f, Neck = 0.2f,
                Head = new Vector3(0.22f, 0.22f, 0.3f), Snout = new Vector3(0.15f, 0.12f, 0.13f), Ear = new Vector3(0.07f, 0.11f, 0.03f),
                TailLength = 0.1f, Tail = 0.1f, TailHangs = true,
                AntlerHeight = 0.45f, AntlerSpread = 0.22f, Features = Feature.Antlers | Feature.Ruff,
                Fur = new Color(0.47f, 0.42f, 0.36f), Light = new Color(0.9f, 0.88f, 0.82f),
                Dark = new Color(0.16f, 0.14f, 0.12f), Accent = new Color(0.72f, 0.64f, 0.5f)
            },
            new Spec
            {
                Name = "Wolf", WalkSpeed = 3.0f, Stride = 0.85f,
                LegHeight = 0.42f, Leg = 0.08f, Body = new Vector3(0.28f, 0.3f, 0.78f),
                NeckRise = 0.1f, NeckForward = 0.08f, Neck = 0.2f,
                Head = new Vector3(0.22f, 0.21f, 0.22f), Snout = new Vector3(0.11f, 0.09f, 0.15f), Ear = new Vector3(0.07f, 0.12f, 0.03f),
                TailLength = 0.36f, Tail = 0.09f,
                Features = Feature.Ruff,
                Fur = new Color(0.48f, 0.48f, 0.5f), Light = new Color(0.78f, 0.78f, 0.76f),
                Dark = new Color(0.18f, 0.18f, 0.2f), Accent = new Color(0.3f, 0.3f, 0.32f)
            },
            new Spec
            {
                Name = "Bear", WalkSpeed = 1.6f, Stride = 1.0f,
                LegHeight = 0.34f, Leg = 0.15f, Body = new Vector3(0.56f, 0.52f, 0.98f),
                NeckRise = 0.04f, NeckForward = 0.1f, Neck = 0.3f,
                Head = new Vector3(0.3f, 0.28f, 0.28f), Snout = new Vector3(0.15f, 0.13f, 0.14f), Ear = new Vector3(0.09f, 0.08f, 0.04f),
                TailLength = 0.06f, Tail = 0.08f,
                Features = Feature.Hump,
                Fur = new Color(0.33f, 0.22f, 0.14f), Light = new Color(0.5f, 0.36f, 0.24f),
                Dark = new Color(0.1f, 0.08f, 0.06f), Accent = new Color(0.27f, 0.18f, 0.11f)
            },
            new Spec
            {
                Name = "Gazelle", WalkSpeed = 3.4f, Stride = 1.0f,
                LegHeight = 0.56f, Leg = 0.055f, Body = new Vector3(0.26f, 0.28f, 0.66f),
                NeckRise = 0.28f, NeckForward = 0.08f, Neck = 0.1f,
                Head = new Vector3(0.14f, 0.14f, 0.21f), Snout = new Vector3(0.09f, 0.08f, 0.09f), Ear = new Vector3(0.055f, 0.12f, 0.02f),
                TailLength = 0.12f, Tail = 0.04f, TailHangs = true,
                HornHeight = 0.26f, Features = Feature.Horns | Feature.FlankStripe,
                Fur = new Color(0.78f, 0.57f, 0.34f), Light = new Color(0.95f, 0.93f, 0.88f),
                Dark = new Color(0.25f, 0.17f, 0.1f), Accent = new Color(0.2f, 0.16f, 0.12f)
            },
            new Spec
            {
                Name = "Jackal", WalkSpeed = 2.8f, Stride = 0.7f,
                LegHeight = 0.34f, Leg = 0.065f, Body = new Vector3(0.22f, 0.25f, 0.6f),
                NeckRise = 0.08f, NeckForward = 0.06f, Neck = 0.15f,
                Head = new Vector3(0.17f, 0.17f, 0.19f), Snout = new Vector3(0.085f, 0.075f, 0.14f), Ear = new Vector3(0.065f, 0.14f, 0.02f),
                TailLength = 0.28f, Tail = 0.08f,
                Features = Feature.Saddle | Feature.TailTip | Feature.DarkTailTip,
                Fur = new Color(0.72f, 0.56f, 0.34f), Light = new Color(0.9f, 0.83f, 0.68f),
                Dark = new Color(0.22f, 0.18f, 0.14f), Accent = new Color(0.38f, 0.33f, 0.3f)
            },
            new Spec
            {
                Name = "SnowHare", Gait = AnimalSpeciesAsset.Gait.Hop, HopLength = 0.7f, HopDuration = 0.3f, HopHeight = 0.22f,
                LegHeight = 0.14f, Leg = 0.06f, Body = new Vector3(0.24f, 0.24f, 0.42f),
                NeckRise = 0.04f, NeckForward = 0.04f, Neck = 0.14f,
                Head = new Vector3(0.16f, 0.16f, 0.18f), Snout = new Vector3(0.09f, 0.07f, 0.06f), Ear = new Vector3(0.055f, 0.24f, 0.025f),
                TailLength = 0.06f, Tail = 0.08f,
                Features = Feature.Haunches,
                Fur = new Color(0.94f, 0.95f, 0.96f), Light = new Color(0.84f, 0.86f, 0.88f),
                Dark = new Color(0.12f, 0.12f, 0.14f), Accent = new Color(0.8f, 0.8f, 0.82f)
            },
            new Spec
            {
                Name = "Jerboa", Gait = AnimalSpeciesAsset.Gait.Hop, HopLength = 0.5f, HopDuration = 0.22f, HopHeight = 0.25f,
                LegHeight = 0.13f, Leg = 0.035f, Body = new Vector3(0.13f, 0.14f, 0.2f),
                NeckRise = 0.02f, NeckForward = 0.02f, Neck = 0.09f,
                Head = new Vector3(0.11f, 0.1f, 0.11f), Snout = new Vector3(0.06f, 0.05f, 0.04f), Ear = new Vector3(0.04f, 0.11f, 0.015f),
                TailLength = 0.34f, Tail = 0.025f,
                Features = Feature.Haunches | Feature.TailTip,
                Fur = new Color(0.82f, 0.67f, 0.44f), Light = new Color(0.96f, 0.93f, 0.86f),
                Dark = new Color(0.15f, 0.1f, 0.08f), Accent = new Color(0.7f, 0.56f, 0.36f)
            },
            new Spec
            {
                Name = "Otter", WalkSpeed = 2.2f, Stride = 0.5f,
                LegHeight = 0.1f, Leg = 0.06f, Body = new Vector3(0.2f, 0.2f, 0.62f),
                NeckRise = 0.03f, NeckForward = 0.05f, Neck = 0.16f,
                Head = new Vector3(0.16f, 0.14f, 0.16f), Snout = new Vector3(0.1f, 0.07f, 0.06f), Ear = new Vector3(0.04f, 0.04f, 0.02f),
                TailLength = 0.36f, Tail = 0.08f,
                Fur = new Color(0.36f, 0.25f, 0.17f), Light = new Color(0.62f, 0.52f, 0.42f),
                Dark = new Color(0.1f, 0.08f, 0.06f), Accent = new Color(0.3f, 0.21f, 0.14f)
            },
            new Spec
            {
                Name = "Duck", Shape = BodyShape.Bird, WalkSpeed = 1.6f, Stride = 0.3f,
                LegHeight = 0.1f, Leg = 0.025f, Body = new Vector3(0.2f, 0.16f, 0.34f),
                NeckRise = 0.08f, Neck = 0.07f, Head = new Vector3(0.1f, 0.1f, 0.12f), Beak = new Vector3(0.07f, 0.025f, 0.08f),
                TailLength = 0.08f,
                Fur = new Color(0.47f, 0.37f, 0.27f), Light = new Color(0.62f, 0.6f, 0.56f),
                Dark = new Color(0.08f, 0.08f, 0.08f), Accent = new Color(0.12f, 0.42f, 0.22f), Bill = new Color(0.9f, 0.68f, 0.18f)
            },
            new Spec
            {
                Name = "Heron", Shape = BodyShape.Bird, WalkSpeed = 1.4f, Stride = 0.6f,
                LegHeight = 0.55f, Leg = 0.025f, Body = new Vector3(0.16f, 0.18f, 0.34f),
                NeckRise = 0.3f, Neck = 0.05f, Head = new Vector3(0.07f, 0.07f, 0.1f), Beak = new Vector3(0.025f, 0.025f, 0.16f),
                TailLength = 0.06f,
                Fur = new Color(0.6f, 0.62f, 0.66f), Light = new Color(0.45f, 0.47f, 0.52f),
                Dark = new Color(0.08f, 0.08f, 0.08f), Accent = new Color(0.92f, 0.92f, 0.92f), Bill = new Color(0.85f, 0.7f, 0.25f)
            },
            new Spec
            {
                Name = "Crow", Shape = BodyShape.Bird, Gait = AnimalSpeciesAsset.Gait.Fly, WalkSpeed = 6f,
                LegHeight = 0.1f, Leg = 0.025f, Body = new Vector3(0.16f, 0.15f, 0.3f),
                NeckRise = 0.03f, Neck = 0.08f, Head = new Vector3(0.1f, 0.1f, 0.11f), Beak = new Vector3(0.04f, 0.04f, 0.08f),
                TailLength = 0.12f, WingSpan = 0.34f, FlightHeight = 5f, Wingbeats = 3f,
                Fur = new Color(0.1f, 0.1f, 0.12f), Light = new Color(0.17f, 0.17f, 0.2f),
                Dark = new Color(0.02f, 0.02f, 0.02f), Accent = new Color(0.12f, 0.12f, 0.14f), Bill = new Color(0.08f, 0.08f, 0.08f)
            },
            new Spec
            {
                Name = "Vulture", Shape = BodyShape.Bird, Gait = AnimalSpeciesAsset.Gait.Fly, WalkSpeed = 7f,
                LegHeight = 0.14f, Leg = 0.03f, Body = new Vector3(0.24f, 0.24f, 0.42f),
                NeckRise = 0.1f, Neck = 0.06f, Head = new Vector3(0.09f, 0.09f, 0.11f), Beak = new Vector3(0.04f, 0.05f, 0.07f),
                TailLength = 0.14f, WingSpan = 0.55f, FlightHeight = 8f, Wingbeats = 1.5f,
                Fur = new Color(0.25f, 0.19f, 0.14f), Light = new Color(0.18f, 0.14f, 0.1f),
                Dark = new Color(0.05f, 0.05f, 0.05f), Accent = new Color(0.78f, 0.62f, 0.55f), Bill = new Color(0.85f, 0.8f, 0.65f)
            },
            new Spec
            {
                Name = "Sparrow", Shape = BodyShape.Bird, Gait = AnimalSpeciesAsset.Gait.Fly, WalkSpeed = 5f,
                LegHeight = 0.06f, Leg = 0.015f, Body = new Vector3(0.09f, 0.08f, 0.15f),
                NeckRise = 0.01f, Neck = 0.05f, Head = new Vector3(0.07f, 0.065f, 0.07f), Beak = new Vector3(0.025f, 0.02f, 0.03f),
                TailLength = 0.07f, WingSpan = 0.13f, FlightHeight = 3f, Wingbeats = 8f,
                Fur = new Color(0.5f, 0.38f, 0.25f), Light = new Color(0.38f, 0.28f, 0.18f),
                Dark = new Color(0.05f, 0.05f, 0.05f), Accent = new Color(0.45f, 0.42f, 0.4f), Bill = new Color(0.25f, 0.2f, 0.15f)
            },
            new Spec
            {
                Name = "Fish", Shape = BodyShape.Fish, WalkSpeed = 1.8f, Stride = 0.4f,
                Body = new Vector3(0.1f, 0.13f, 0.3f), SwimHeight = 0.42f,
                Fur = new Color(0.42f, 0.5f, 0.42f), Light = new Color(0.82f, 0.82f, 0.74f),
                Dark = new Color(0.06f, 0.06f, 0.06f), Accent = new Color(0.55f, 0.42f, 0.32f)
            },
        };

        private struct Box
        {
            public string Role;
            public Vector3 Centre;
            public Vector3 Size;
            public AnimalSpeciesAsset.PartMotion Motion;
            public Vector3 Pivot;
        }

        [MenuItem("Eco/Rebuild animal models")]
        public static void Build()
        {
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            foreach (Spec spec in Species)
            {
                var materials = new Dictionary<string, Material>
                {
                    [FurRole] = SaveMaterial($"Animal{spec.Name}_{FurRole}", spec.Fur),
                    [LightRole] = SaveMaterial($"Animal{spec.Name}_{LightRole}", spec.Light),
                    [DarkRole] = SaveMaterial($"Animal{spec.Name}_{DarkRole}", spec.Dark),
                    [AccentRole] = SaveMaterial($"Animal{spec.Name}_{AccentRole}", spec.Accent)
                };
                if (spec.Shape == BodyShape.Bird) materials[BillRole] = SaveMaterial($"Animal{spec.Name}_{BillRole}", spec.Bill);

                Write(spec, Layout(spec), cube, materials);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Animal models rebuilt for {Species.Length} species.");
        }

        private static List<Box> Layout(Spec s)
        {
            if (s.Shape == BodyShape.Bird) return BirdLayout(s);
            if (s.Shape == BodyShape.Fish) return FishLayout(s);

            var boxes = new List<Box>();
            float bodyTop = s.LegHeight + s.Body.y;
            float front = s.Body.z / 2f;
            float back = -s.Body.z / 2f;
            float bodyMiddle = s.LegHeight + s.Body.y / 2f;

            Add(boxes, FurRole, new Vector3(0f, bodyMiddle, 0f), s.Body);
            Add(boxes, LightRole, new Vector3(0f, s.LegHeight + 0.02f, 0f), new Vector3(s.Body.x * 0.8f, 0.06f, s.Body.z * 0.75f));

            // Legs: a front pair, and a hind pair or, for a hopping animal, haunches and long feet.
            float legX = s.Body.x / 2f - s.Leg / 2f - 0.005f;
            float frontZ = front - s.Leg * 0.7f;
            float hindZ = back + s.Leg * 0.7f;
            AddLegPair(boxes, s, legX, frontZ, AnimalSpeciesAsset.PartMotion.FrontLeg);
            if ((s.Features & Feature.Haunches) != 0) AddHaunches(boxes, s, back);
            else AddLegPair(boxes, s, legX, hindZ, AnimalSpeciesAsset.PartMotion.HindLeg);

            // Neck, head, snout, nose, eyes.
            if (s.NeckRise > 0.02f)
            {
                float neckHeight = s.NeckRise + 0.1f;
                Add(boxes, FurRole, new Vector3(0f, bodyTop - 0.06f + neckHeight / 2f, front + (s.NeckForward - s.Neck) / 2f),
                    new Vector3(s.Neck * 0.95f, neckHeight, s.Neck + s.NeckForward));
            }

            var head = new Vector3(0f, bodyTop + s.NeckRise + s.Head.y / 2f - 0.04f, front + s.NeckForward + s.Head.z / 2f - s.Neck * 0.4f);
            Add(boxes, FurRole, head, s.Head);
            float headFront = head.z + s.Head.z / 2f;
            float headTop = head.y + s.Head.y / 2f;
            float headBottom = head.y - s.Head.y / 2f;

            var snout = new Vector3(0f, headBottom + s.Snout.y / 2f + 0.005f, headFront + s.Snout.z / 2f - 0.02f);
            Add(boxes, LightRole, snout, s.Snout);
            Add(boxes, DarkRole, new Vector3(0f, snout.y + s.Snout.y / 2f - 0.015f, snout.z + s.Snout.z / 2f + 0.004f),
                new Vector3(s.Snout.x * 0.45f, 0.035f, 0.02f));
            foreach (float side in Sides)
            {
                Add(boxes, DarkRole, new Vector3(side * (s.Head.x / 2f + 0.004f), head.y + s.Head.y * 0.12f, head.z + s.Head.z * 0.18f),
                    new Vector3(0.012f, 0.035f, 0.035f));
            }

            // Ears, each with its lighter inside.
            foreach (float side in Sides)
            {
                var ear = new Vector3(side * (s.Head.x / 2f - s.Ear.x / 2f - 0.005f), headTop + s.Ear.y / 2f - 0.01f, head.z - s.Head.z * 0.2f);
                var pivot = new Vector3(ear.x, headTop, ear.z);
                Add(boxes, FurRole, ear, s.Ear, AnimalSpeciesAsset.PartMotion.Ear, pivot);
                Add(boxes, LightRole, new Vector3(ear.x, ear.y - s.Ear.y * 0.05f, ear.z + s.Ear.z / 2f + 0.003f),
                    new Vector3(s.Ear.x * 0.55f, s.Ear.y * 0.7f, 0.006f), AnimalSpeciesAsset.PartMotion.Ear, pivot);
            }

            AddTail(boxes, s, bodyTop, back);
            AddFeatures(boxes, s, head, headTop, bodyTop, front, back);
            return boxes;
        }

        private static readonly float[] Sides = { 1f, -1f };

        // A leg with its hoof or paw, swinging about the top of the leg.
        private static void AddLegPair(List<Box> boxes, Spec s, float legX, float z, AnimalSpeciesAsset.PartMotion motion)
        {
            float hoof = Mathf.Max(0.03f, Mathf.Min(0.07f, s.LegHeight * 0.18f));
            float top = s.LegHeight + 0.05f;
            foreach (float side in Sides)
            {
                var pivot = new Vector3(side * legX, s.LegHeight, z);
                Add(boxes, FurRole, new Vector3(side * legX, (hoof + top) / 2f, z), new Vector3(s.Leg, top - hoof, s.Leg), motion, pivot);
                Add(boxes, DarkRole, new Vector3(side * legX, hoof / 2f, z), new Vector3(s.Leg + 0.012f, hoof, s.Leg + 0.012f), motion, pivot);
            }
        }

        // A hopping animal's hind legs: big haunches against the body and long flat feet.
        private static void AddHaunches(List<Box> boxes, Spec s, float back)
        {
            float width = s.Body.x * 0.3f;
            float z = back + s.Body.z * 0.22f;
            float bottom = s.LegHeight * 0.5f;
            float top = s.LegHeight + s.Body.y * 0.65f;
            foreach (float side in Sides)
            {
                float x = side * (s.Body.x / 2f - width / 2f + 0.015f);
                var pivot = new Vector3(x, s.LegHeight + s.Body.y * 0.5f, z);
                Add(boxes, FurRole, new Vector3(x, (bottom + top) / 2f, z), new Vector3(width, top - bottom, s.Body.z * 0.4f),
                    AnimalSpeciesAsset.PartMotion.HindLeg, pivot);
                Add(boxes, LightRole, new Vector3(side * (s.Body.x / 2f - width / 2f), 0.025f, z + 0.04f),
                    new Vector3(width * 0.6f, 0.05f, s.Body.z * 0.45f), AnimalSpeciesAsset.PartMotion.HindLeg, pivot);
            }
        }

        // A tail straight out behind, or hanging down; with a tip of another colour if asked.
        private static void AddTail(List<Box> boxes, Spec s, float bodyTop, float back)
        {
            Vector3 centre;
            Vector3 size;
            Vector3 pivot;
            if (s.TailHangs)
            {
                centre = new Vector3(0f, bodyTop - 0.03f - s.TailLength / 2f, back - s.Tail / 2f + 0.02f);
                size = new Vector3(s.Tail, s.TailLength, s.Tail);
                pivot = new Vector3(0f, bodyTop - 0.03f, back);
            }
            else
            {
                centre = new Vector3(0f, bodyTop - s.Tail / 2f - 0.03f, back - s.TailLength / 2f + 0.03f);
                size = new Vector3(s.Tail, s.Tail, s.TailLength);
                pivot = new Vector3(0f, centre.y, back);
            }

            string role = (s.Features & Feature.Haunches) != 0 && s.TailLength < 0.1f ? LightRole : FurRole;
            Add(boxes, role, centre, size, AnimalSpeciesAsset.PartMotion.Tail, pivot);
            if ((s.Features & Feature.TailTip) == 0 || s.TailHangs) return;

            string tipRole = (s.Features & Feature.DarkTailTip) != 0 ? DarkRole : LightRole;
            float end = back - s.TailLength + 0.03f;
            float tip = Mathf.Min(0.1f, s.TailLength * 0.35f);
            Add(boxes, tipRole, new Vector3(0f, centre.y, end + tip / 2f - 0.01f), new Vector3(s.Tail * 1.6f + 0.01f, s.Tail * 1.6f + 0.01f, tip),
                AnimalSpeciesAsset.PartMotion.Tail, pivot);
        }

        private static void AddFeatures(List<Box> boxes, Spec s, Vector3 head, float headTop, float bodyTop, float front, float back)
        {
            if ((s.Features & Feature.Antlers) != 0)
            {
                float z = head.z - s.Head.z * 0.1f;
                float h = s.AntlerHeight;
                foreach (float side in Sides)
                {
                    float x = side * (s.Head.x / 2f - 0.03f);
                    float reach = side * (s.Head.x / 2f + s.AntlerSpread);
                    Add(boxes, AccentRole, new Vector3(x, headTop + h * 0.5f, z), new Vector3(0.035f, h, 0.035f));
                    Add(boxes, AccentRole, new Vector3((x + reach) / 2f, headTop + h * 0.55f, z), new Vector3(Mathf.Abs(reach - x), 0.03f, 0.03f));
                    Add(boxes, AccentRole, new Vector3(reach, headTop + h * 0.8f, z - 0.02f), new Vector3(0.03f, h * 0.5f, 0.03f));
                    Add(boxes, AccentRole, new Vector3(x, headTop + h * 0.75f, z + 0.06f), new Vector3(0.03f, 0.03f, 0.12f));
                }
            }

            if ((s.Features & Feature.Horns) != 0)
            {
                float z = head.z - s.Head.z * 0.15f;
                foreach (float side in Sides)
                {
                    float x = side * (s.Head.x / 2f - 0.035f);
                    Add(boxes, AccentRole, new Vector3(x, headTop + s.HornHeight / 2f, z - 0.02f), new Vector3(0.03f, s.HornHeight, 0.03f));
                    Add(boxes, AccentRole, new Vector3(x, headTop + s.HornHeight + 0.05f, z - 0.065f), new Vector3(0.025f, 0.12f, 0.025f));
                }
            }

            if ((s.Features & Feature.Ruff) != 0)
            {
                Add(boxes, LightRole, new Vector3(0f, bodyTop - s.Body.y * 0.45f, front + 0.02f), new Vector3(s.Body.x + 0.04f, s.Body.y * 0.9f, 0.22f));
            }

            if ((s.Features & Feature.Hump) != 0)
            {
                Add(boxes, FurRole, new Vector3(0f, bodyTop + 0.05f, front - s.Body.z * 0.25f), new Vector3(s.Body.x * 0.8f, 0.12f, s.Body.z * 0.35f));
            }

            if ((s.Features & Feature.FlankStripe) != 0)
            {
                foreach (float side in Sides)
                {
                    Add(boxes, DarkRole, new Vector3(side * (s.Body.x / 2f + 0.003f), s.LegHeight + s.Body.y * 0.3f, 0f), new Vector3(0.008f, 0.05f, s.Body.z * 0.75f));
                }
            }

            if ((s.Features & Feature.Saddle) != 0)
            {
                Add(boxes, AccentRole, new Vector3(0f, bodyTop + 0.005f, -0.02f), new Vector3(s.Body.x * 0.7f, 0.02f, s.Body.z * 0.6f));
            }
        }

        // A bird: a body on two thin legs (both swing as front legs, so they move in turn), wings along
        // its sides, a neck rising from its front, a head in the accent colour with its beak and eyes,
        // and a short tail.
        private static List<Box> BirdLayout(Spec s)
        {
            var boxes = new List<Box>();
            float bodyTop = s.LegHeight + s.Body.y;
            float front = s.Body.z / 2f;
            float back = -s.Body.z / 2f;

            Add(boxes, FurRole, new Vector3(0f, s.LegHeight + s.Body.y / 2f, 0f), s.Body);
            foreach (float side in Sides)
            {
                if (s.Gait == AnimalSpeciesAsset.Gait.Fly)
                {
                    // A flying bird's wing hangs from a hinge along the top of its side, WingSpan long:
                    // AnimalViews raises and beats it in flight, folds it at rest.
                    var hinge = new Vector3(side * (s.Body.x / 2f + 0.01f), s.LegHeight + s.Body.y * 0.95f, -0.02f);
                    Add(boxes, LightRole, new Vector3(hinge.x, hinge.y - s.WingSpan / 2f, hinge.z), new Vector3(0.02f, s.WingSpan, s.Body.z * 0.75f),
                        AnimalSpeciesAsset.PartMotion.Wing, hinge);
                }
                else
                {
                    Add(boxes, LightRole, new Vector3(side * (s.Body.x / 2f + 0.01f), s.LegHeight + s.Body.y * 0.6f, -0.02f),
                        new Vector3(0.02f, s.Body.y * 0.6f, s.Body.z * 0.7f));
                }

                float x = side * s.Body.x * 0.22f;
                var pivot = new Vector3(x, s.LegHeight, 0f);
                Add(boxes, BillRole, new Vector3(x, (s.LegHeight + 0.03f) / 2f, 0f), new Vector3(s.Leg, s.LegHeight + 0.03f, s.Leg),
                    AnimalSpeciesAsset.PartMotion.FrontLeg, pivot);
                Add(boxes, BillRole, new Vector3(x, 0.01f, s.Leg), new Vector3(s.Leg * 2.5f, 0.02f, s.Leg * 3f),
                    AnimalSpeciesAsset.PartMotion.FrontLeg, pivot);
            }

            float neckZ = front - s.Neck / 2f + 0.02f;
            Add(boxes, FurRole, new Vector3(0f, bodyTop + s.NeckRise / 2f - 0.03f, neckZ), new Vector3(s.Neck, s.NeckRise + 0.06f, s.Neck));

            var head = new Vector3(0f, bodyTop + s.NeckRise + s.Head.y / 2f - 0.04f, neckZ + s.Head.z / 2f - s.Neck * 0.3f);
            Add(boxes, AccentRole, head, s.Head);
            Add(boxes, BillRole, new Vector3(0f, head.y - s.Head.y * 0.1f, head.z + s.Head.z / 2f + s.Beak.z / 2f - 0.01f), s.Beak);
            foreach (float side in Sides)
            {
                Add(boxes, DarkRole, new Vector3(side * (s.Head.x / 2f + 0.003f), head.y + s.Head.y * 0.15f, head.z + s.Head.z * 0.15f),
                    new Vector3(0.008f, 0.02f, 0.02f));
            }

            Add(boxes, FurRole, new Vector3(0f, bodyTop - 0.03f, back - s.TailLength / 2f + 0.02f), new Vector3(s.Body.x * 0.6f, 0.04f, s.TailLength),
                AnimalSpeciesAsset.PartMotion.Tail, new Vector3(0f, bodyTop - 0.03f, back));
            return boxes;
        }

        // A fish: a body with a lighter belly, a tail fin that sways, a dorsal fin and eyes, its centre
        // SwimHeight above the bottom of the water block it swims in.
        private static List<Box> FishLayout(Spec s)
        {
            var boxes = new List<Box>();
            float y = s.SwimHeight;
            float back = -s.Body.z / 2f;
            Add(boxes, FurRole, new Vector3(0f, y, 0f), s.Body);
            Add(boxes, LightRole, new Vector3(0f, y - s.Body.y * 0.35f, 0.01f), new Vector3(s.Body.x * 0.9f, s.Body.y * 0.35f, s.Body.z * 0.8f));
            Add(boxes, AccentRole, new Vector3(0f, y, back - 0.04f), new Vector3(0.02f, s.Body.y * 1.1f, 0.1f),
                AnimalSpeciesAsset.PartMotion.Tail, new Vector3(0f, y, back));
            Add(boxes, AccentRole, new Vector3(0f, y + s.Body.y / 2f + 0.025f, -0.02f), new Vector3(0.02f, 0.06f, s.Body.z * 0.35f));
            foreach (float side in Sides)
            {
                Add(boxes, DarkRole, new Vector3(side * (s.Body.x / 2f + 0.003f), y + s.Body.y * 0.15f, s.Body.z * 0.35f), new Vector3(0.008f, 0.025f, 0.025f));
            }

            return boxes;
        }

        private static void Add(List<Box> boxes, string role, Vector3 centre, Vector3 size,
            AnimalSpeciesAsset.PartMotion motion = AnimalSpeciesAsset.PartMotion.Body, Vector3 pivot = default)
        {
            boxes.Add(new Box { Role = role, Centre = centre, Size = size, Motion = motion, Pivot = pivot });
        }

        // Writes the parts, the rig, the gait and the aiming size into the species asset.
        private static void Write(Spec spec, List<Box> boxes, Mesh cube, Dictionary<string, Material> materials)
        {
            string path = $"{SpeciesFolder}/{spec.Name}.asset";
            var species = AssetDatabase.LoadAssetAtPath<AnimalSpeciesAsset>(path);
            if (species == null)
            {
                species = ScriptableObject.CreateInstance<AnimalSpeciesAsset>();
                AssetDatabase.CreateAsset(species, path);
            }

            var serialized = new SerializedObject(species);
            SerializedProperty parts = serialized.FindProperty("parts");
            SerializedProperty rig = serialized.FindProperty("rig");
            parts.arraySize = boxes.Count;
            rig.arraySize = boxes.Count;

            float height = 0f;
            float width = 0f;
            for (int i = 0; i < boxes.Count; i++)
            {
                Box box = boxes[i];
                SerializedProperty part = parts.GetArrayElementAtIndex(i);
                part.FindPropertyRelative("mesh").objectReferenceValue = cube;
                part.FindPropertyRelative("material").objectReferenceValue = materials[box.Role];
                part.FindPropertyRelative("offset").vector3Value = box.Centre;
                part.FindPropertyRelative("scale").vector3Value = box.Size;

                SerializedProperty entry = rig.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("motion").enumValueIndex = (int)box.Motion;
                entry.FindPropertyRelative("pivot").vector3Value = box.Pivot;

                height = Mathf.Max(height, box.Centre.y + box.Size.y / 2f);
                width = Mathf.Max(width, 2f * (Mathf.Abs(box.Centre.x) + box.Size.x / 2f));
            }

            serialized.FindProperty("height").floatValue = Mathf.Round(height * 100f) / 100f;
            serialized.FindProperty("width").floatValue = Mathf.Round(Mathf.Max(width, spec.Body.z * 0.8f) * 100f) / 100f;
            serialized.FindProperty("gait").enumValueIndex = (int)spec.Gait;
            serialized.FindProperty("walkSpeed").floatValue = spec.WalkSpeed;
            serialized.FindProperty("strideLength").floatValue = spec.Stride;
            serialized.FindProperty("hopLength").floatValue = spec.HopLength;
            serialized.FindProperty("hopDuration").floatValue = spec.HopDuration;
            serialized.FindProperty("hopHeight").floatValue = spec.HopHeight;
            serialized.FindProperty("flightHeight").floatValue = spec.FlightHeight;
            serialized.FindProperty("wingbeatsPerSecond").floatValue = spec.Wingbeats;
            if (spec.WingSpan > 0f) serialized.FindProperty("wingFold").floatValue = Mathf.Clamp(spec.Body.y * 0.8f / spec.WingSpan, 0.1f, 1f);
            serialized.FindProperty("bodyPivot").vector3Value = new Vector3(0f, spec.LegHeight + spec.Body.y / 2f, 0f);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(species);
        }

        private static Material SaveMaterial(string name, Color colour)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", colour);
            material.SetColor("_Color", colour);
            material.SetFloat("_Smoothness", 0.1f);
            material.enableInstancing = true;
            EditorUtility.SetDirty(material);
            return material;
        }
    }
}
