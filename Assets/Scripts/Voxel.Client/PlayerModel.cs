using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Tile = Voxel.Client.PlayerSkin.Tile;

namespace Voxel.Client
{
    // Block-style body of a player, built from boxes at runtime, and its animation. Each joint (hips,
    // spine, neck, shoulders, elbows, thighs, knees) is a pivot carrying one mesh made of the boxes it
    // moves. The animation is driven by how the player object moves, so it works the same for the
    // owner and for the copies that follow the NetworkTransform. The only thing sent for it is the
    // pitch of the gaze (the NetworkTransform already carries the yaw), which the head follows.
    // The owner's camera sits at the eyes of this head (EyeOffset), so the owner sees its own body
    // when looking down; only its head is hidden, keeping its shadow.
    public class PlayerModel : NetworkBehaviour
    {
        // Heights in blocks: feet at 0, eyes near PlayerRules.EyeHeight (1.62), top of the hair at 1.82.
        private const float HipHeight = 0.86f;
        private const float ThighLength = 0.42f;
        private const float ShinLength = 0.36f;
        private const float PelvisHeight = 0.12f;
        private const float ChestHeight = 0.46f;
        private const float ChestWidth = 0.44f;
        private const float ChestDepth = 0.24f;
        private const float HeadSize = 0.30f;
        private const float UpperArmLength = 0.28f;

        // Ground covered by one step, horizontal speed of a full walk, and the swings it gives.
        private const float StrideLength = 1.1f;
        private const float WalkSpeed = 5f;
        private const float ThighSwing = 32f;
        private const float KneeBend = 50f;
        private const float ArmSwing = 30f;
        private const float ElbowBend = 30f;
        private const float BobHeight = 0.035f;

        // Vertical speed at which the jump pose is complete.
        private const float AirborneSpeed = 4f;

        // How fast the measured speeds follow the movement: remote copies move in small jumps.
        private const float Smoothing = 12f;

        // A move longer than this in one frame is a teleport, not a run.
        private const float TeleportDistance = 2f;

        // How far the head tilts to follow the gaze.
        private const float MaxHeadPitch = 70f;

        // Height of the neck joint, and the point between the eyes relative to it, slightly inside the
        // head so that the near plane never cuts its front.
        private const float NeckHeight = HipHeight + PelvisHeight + ChestHeight;
        private static readonly Vector3 EyesInHead = new Vector3(0f, 0.19f, 0.1f);

        [SerializeField] private Material material;

        // Pitch of the owner's gaze in whole degrees, positive downwards: one byte, sent only when it changes.
        private readonly NetworkVariable<sbyte> lookPitch = new NetworkVariable<sbyte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private Transform model;
        private Transform hips;
        private Transform spine;
        private Transform head;
        private Transform leftUpperArm;
        private Transform rightUpperArm;
        private Transform leftForearm;
        private Transform rightForearm;
        private Transform leftThigh;
        private Transform rightThigh;
        private Transform leftShin;
        private Transform rightShin;
        private Material skinMaterial;
        private Texture2D skinTexture;

        private Vector3 lastPosition;
        private float speed;
        private float verticalSpeed;
        private float phase;
        private float pitch;
        private float ownPitch;

        // Position of the eyes relative to the player's feet, in the player's own frame, for a gaze
        // pitch: the head turns about the neck, so the eyes move forward and down when looking down.
        // Looking straight down they stay 0.21 forward, inside the player's collision box, in front of the chest.
        public static Vector3 EyeOffset(float pitch)
        {
            return new Vector3(0f, NeckHeight, 0f) + Quaternion.Euler(HeadPitch(pitch), 0f, 0f) * EyesInHead;
        }

        private static float HeadPitch(float pitch) => Mathf.Clamp(pitch, -MaxHeadPitch, MaxHeadPitch);

        // Called by the owner's controller whenever its gaze moves.
        public void SetLookPitch(float degrees)
        {
            ownPitch = degrees;
            var whole = (sbyte)Mathf.Clamp(Mathf.RoundToInt(degrees), sbyte.MinValue, sbyte.MaxValue);
            if (IsSpawned && IsOwner && lookPitch.Value != whole) lookPitch.Value = whole;
        }

        private void Awake()
        {
            Build();
            Skin(PlayerSkin.ShirtFor(0));
            lastPosition = transform.position;
        }

        public override void OnNetworkSpawn()
        {
            Skin(PlayerSkin.ShirtFor(OwnerClientId));

            // The owner's camera is inside the head: the head would fill the view.
            if (IsOwner) head.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            lastPosition = transform.position;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            foreach (Mesh mesh in meshes)
            {
                Destroy(mesh);
            }

            if (skinMaterial != null) Destroy(skinMaterial);
            if (skinTexture != null) Destroy(skinTexture);
        }

        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            Vector3 position = transform.position;
            Vector3 moved = position - lastPosition;
            lastPosition = position;
            if (moved.sqrMagnitude > TeleportDistance * TeleportDistance)
            {
                moved = Vector3.zero;
                speed = 0f;
                verticalSpeed = 0f;
            }

            float follow = 1f - Mathf.Exp(-Smoothing * deltaTime);
            speed = Mathf.Lerp(speed, new Vector2(moved.x, moved.z).magnitude / deltaTime, follow);
            verticalSpeed = Mathf.Lerp(verticalSpeed, moved.y / deltaTime, follow);
            phase = Mathf.Repeat(phase + speed * deltaTime * Mathf.PI / StrideLength, 2f * Mathf.PI);

            // The owner follows its own gaze exactly; the others smooth the whole degrees they receive.
            float targetPitch = IsSpawned && !IsOwner ? lookPitch.Value : ownPitch;
            pitch = IsSpawned && !IsOwner ? Mathf.Lerp(pitch, targetPitch, follow) : targetPitch;

            ApplyPose(Time.time);
        }

        // Poses the joints for the current speeds. Walking swings legs and arms in opposition, bends
        // the knee of the leg moving forward, and twists the shoulders against the hips. In the air
        // one knee comes up and the arms spread. Standing still the player breathes. The head follows the
        // gaze; the chest does not, so that the owner looking down sees its body from the front.
        // Positive rotations about X move a hanging limb backwards and tilt the head down; about Z, towards +X.
        private void ApplyPose(float time)
        {
            float walk = Mathf.Clamp01(speed / WalkSpeed);
            float airborne = Mathf.Clamp01(Mathf.Abs(verticalSpeed) / AirborneSpeed);
            float grounded = 1f - airborne;
            float idle = (1f - walk) * grounded;
            float swing = Mathf.Sin(phase) * walk * grounded;
            float stride = Mathf.Cos(phase) * walk * grounded;
            float sway = Mathf.Sin(time * 1.5f) * 2f * idle;
            float lean = 5f * walk * grounded;
            model.localPosition = new Vector3(0f, Mathf.Abs(Mathf.Sin(phase)) * BobHeight * walk * grounded, 0f);
            hips.localRotation = Quaternion.Euler(0f, -swing * 4f, 0f);
            spine.localRotation = Quaternion.Euler(lean + Mathf.Sin(time * 2.2f) * idle, swing * 7f, 0f);
            head.localRotation = Quaternion.Euler(HeadPitch(pitch) - lean + Mathf.Sin(phase * 2f) * 2f * walk, 0f, 0f);

            leftThigh.localRotation = Quaternion.Euler(swing * ThighSwing - airborne * 40f, 0f, 0f);
            rightThigh.localRotation = Quaternion.Euler(-swing * ThighSwing + airborne * 10f, 0f, 0f);
            leftShin.localRotation = Quaternion.Euler(Mathf.Max(0f, -stride) * KneeBend + airborne * 65f, 0f, 0f);
            rightShin.localRotation = Quaternion.Euler(Mathf.Max(0f, stride) * KneeBend + airborne * 25f, 0f, 0f);

            leftUpperArm.localRotation = Quaternion.Euler(-swing * ArmSwing - airborne * 35f, 0f, -sway - airborne * 40f);
            rightUpperArm.localRotation = Quaternion.Euler(swing * ArmSwing - airborne * 35f, 0f, sway + airborne * 40f);
            leftForearm.localRotation = Quaternion.Euler(-(10f + Mathf.Max(0f, swing) * ElbowBend + airborne * 25f), 0f, 0f);
            rightForearm.localRotation = Quaternion.Euler(-(10f + Mathf.Max(0f, -swing) * ElbowBend + airborne * 25f), 0f, 0f);
        }

        private void Skin(Color shirt)
        {
            if (skinTexture != null) Destroy(skinTexture);
            skinTexture = PlayerSkin.CreateTexture(shirt);
            if (skinMaterial == null)
            {
                skinMaterial = new Material(material) { name = "PlayerSkin" };
                foreach (MeshRenderer part in renderers)
                {
                    part.sharedMaterial = skinMaterial;
                }
            }

            skinMaterial.mainTexture = skinTexture;
        }

        // Positions are relative to the joint that carries each box. +X is the player's right, +Z its front.
        private void Build()
        {
            model = new GameObject("Model").transform;
            model.SetParent(transform, false);

            hips = Joint("Hips", model, new Vector3(0f, HipHeight, 0f), new BoxBuilder()
                .Add(new Vector3(0f, PelvisHeight / 2f, 0f), new Vector3(0.40f, PelvisHeight, ChestDepth),
                    new Faces(Tile.BeltFront, Tile.Belt, Tile.Belt, Tile.Trousers, Tile.Trousers)));

            leftThigh = Thigh("LeftThigh", -0.1f);
            rightThigh = Thigh("RightThigh", 0.1f);
            leftShin = Shin("LeftShin", leftThigh);
            rightShin = Shin("RightShin", rightThigh);

            float chestTop = ChestHeight;
            float strapX = 0.15f;
            spine = Joint("Spine", hips, new Vector3(0f, PelvisHeight, 0f), new BoxBuilder()
                .Add(new Vector3(0f, ChestHeight / 2f, 0f), new Vector3(ChestWidth, ChestHeight, ChestDepth),
                    new Faces(Tile.ChestFront, Tile.ChestBack, Tile.ChestSide, Tile.Shirt, Tile.Shirt))
                .Add(new Vector3(-strapX, 0.25f, ChestDepth / 2f + 0.006f), new Vector3(0.045f, 0.40f, 0.012f), Faces.All(Tile.Strap))
                .Add(new Vector3(strapX, 0.25f, ChestDepth / 2f + 0.006f), new Vector3(0.045f, 0.40f, 0.012f), Faces.All(Tile.Strap))
                .Add(new Vector3(-strapX, chestTop + 0.006f, 0f), new Vector3(0.045f, 0.012f, ChestDepth + 0.024f), Faces.All(Tile.Strap))
                .Add(new Vector3(strapX, chestTop + 0.006f, 0f), new Vector3(0.045f, 0.012f, ChestDepth + 0.024f), Faces.All(Tile.Strap))
                .Add(new Vector3(0f, 0.22f, -ChestDepth / 2f - 0.07f), new Vector3(0.32f, 0.36f, 0.14f),
                    new Faces(Tile.Canvas, Tile.PackBack, Tile.PackSide, Tile.PackTop, Tile.Canvas)));

            // The neck belongs to the head, which the owner does not see: it would sit right under its eyes.
            head = Joint("Head", spine, new Vector3(0f, chestTop, 0f), new BoxBuilder()
                .Add(new Vector3(0f, 0.02f, 0f), new Vector3(0.13f, 0.06f, 0.13f), Faces.All(Tile.SkinShadow))
                .Add(new Vector3(0f, 0.03f + HeadSize / 2f, 0f), Vector3.one * HeadSize,
                    new Faces(Tile.Face, Tile.HeadBack, Tile.HeadSide, Tile.Hair, Tile.SkinShadow))
                .Add(new Vector3(0f, 0.3575f, -0.005f), new Vector3(0.32f, 0.055f, 0.32f), new Faces(Tile.HairEdge, Tile.HairEdge, Tile.HairEdge, Tile.Hair, Tile.Hair))
                .Add(new Vector3(0f, 0.24f, -0.16f), new Vector3(0.32f, 0.2f, 0.03f), Faces.All(Tile.HairEdge))
                .Add(new Vector3(0f, 0.305f, 0.155f), new Vector3(0.3f, 0.05f, 0.02f), Faces.All(Tile.HairEdge))
                .Add(new Vector3(0f, 0.166f, 0.165f), new Vector3(0.045f, 0.04f, 0.03f), Faces.All(Tile.Skin))
                .Add(new Vector3(-0.16f, 0.17f, -0.01f), new Vector3(0.025f, 0.07f, 0.05f), Faces.All(Tile.Skin))
                .Add(new Vector3(0.16f, 0.17f, -0.01f), new Vector3(0.025f, 0.07f, 0.05f), Faces.All(Tile.Skin)));

            float shoulderX = ChestWidth / 2f + 0.065f;
            leftUpperArm = UpperArm("LeftUpperArm", -shoulderX);
            rightUpperArm = UpperArm("RightUpperArm", shoulderX);
            leftForearm = Forearm("LeftForearm", leftUpperArm);
            rightForearm = Forearm("RightForearm", rightUpperArm);
        }

        private Transform Thigh(string jointName, float x)
        {
            return Joint(jointName, hips, new Vector3(x, 0f, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -ThighLength / 2f, 0f), new Vector3(0.17f, ThighLength, 0.18f),
                    new Faces(Tile.TrouserFront, Tile.Trousers, Tile.TrouserSide, Tile.Trousers, Tile.Trousers)));
        }

        // The shin carries the shoe, which sticks out forward.
        private Transform Shin(string jointName, Transform thigh)
        {
            return Joint(jointName, thigh, new Vector3(0f, -ThighLength, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -ShinLength / 2f, 0f), new Vector3(0.15f, ShinLength, 0.16f),
                    new Faces(Tile.ShinFront, Tile.ShinFront, Tile.ShinSide, Tile.Trousers, Tile.Trousers))
                .Add(new Vector3(0f, -ShinLength - 0.04f, 0.035f), new Vector3(0.16f, 0.08f, 0.27f),
                    new Faces(Tile.ShoeFront, Tile.ShoeFront, Tile.ShoeSide, Tile.ShoeTop, Tile.Sole)));
        }

        private Transform UpperArm(string jointName, float x)
        {
            return Joint(jointName, spine, new Vector3(x, ChestHeight - 0.04f, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -0.13f, 0f), new Vector3(0.13f, 0.32f, 0.13f),
                    new Faces(Tile.Sleeve, Tile.Sleeve, Tile.Sleeve, Tile.Shirt, Tile.Skin)));
        }

        // The forearm carries the hand.
        private Transform Forearm(string jointName, Transform upperArm)
        {
            return Joint(jointName, upperArm, new Vector3(0f, -UpperArmLength, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -0.12f, 0f), new Vector3(0.11f, 0.25f, 0.11f), new Faces(Tile.Forearm, Tile.Forearm, Tile.Forearm, Tile.Skin, Tile.Skin))
                .Add(new Vector3(0f, -0.3f, 0.005f), new Vector3(0.12f, 0.11f, 0.085f), new Faces(Tile.Hand, Tile.Hand, Tile.Hand, Tile.Hand, Tile.Fingers)));
        }

        private Transform Joint(string jointName, Transform parent, Vector3 position, BoxBuilder boxes)
        {
            var joint = new GameObject(jointName).transform;
            joint.SetParent(parent, false);
            joint.localPosition = position;

            Mesh mesh = boxes.ToMesh(jointName);
            meshes.Add(mesh);
            joint.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderers.Add(joint.gameObject.AddComponent<MeshRenderer>());
            return joint;
        }

        // Tiles of the six faces of a box. Both side faces share one tile, oriented from back to front.
        private readonly struct Faces
        {
            public readonly Tile Front;
            public readonly Tile Back;
            public readonly Tile Side;
            public readonly Tile Top;
            public readonly Tile Bottom;

            public Faces(Tile front, Tile back, Tile side, Tile top, Tile bottom)
            {
                Front = front;
                Back = back;
                Side = side;
                Top = top;
                Bottom = bottom;
            }

            public static Faces All(Tile tile) => new Faces(tile, tile, tile, tile, tile);
        }

        // Gathers boxes into one mesh, each face mapped to its tile of the player texture.
        private sealed class BoxBuilder
        {
            private static readonly Vector3[] Normals = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down };
            private static readonly Vector3[] Ups = { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.forward, Vector3.forward };

            private readonly List<Vector3> vertices = new List<Vector3>();
            private readonly List<Vector3> normals = new List<Vector3>();
            private readonly List<Vector2> uvs = new List<Vector2>();
            private readonly List<int> triangles = new List<int>();

            public BoxBuilder Add(Vector3 centre, Vector3 size, Faces faces)
            {
                Tile[] tiles = { faces.Front, faces.Back, faces.Side, faces.Side, faces.Top, faces.Bottom };
                Vector3 half = size / 2f;
                for (int face = 0; face < 6; face++)
                {
                    Vector3 normal = Normals[face];
                    Vector3 up = Ups[face];

                    // Right as seen from outside the face: corners listed clockwise, as Unity expects.
                    Vector3 right = Vector3.Cross(normal, up);
                    Vector3 outward = normal * Mathf.Abs(Vector3.Dot(normal, half));
                    Vector3 across = right * Mathf.Abs(Vector3.Dot(right, half));
                    Vector3 along = up * Mathf.Abs(Vector3.Dot(up, half));
                    Rect uv = PlayerSkin.UvRect(tiles[face]);

                    // On the left face, right points backwards: flip the tile so that it runs back to front.
                    float uLeft = face == 3 ? uv.xMax : uv.xMin;
                    float uRight = face == 3 ? uv.xMin : uv.xMax;

                    int first = vertices.Count;
                    vertices.Add(centre + outward - across - along);
                    vertices.Add(centre + outward - across + along);
                    vertices.Add(centre + outward + across + along);
                    vertices.Add(centre + outward + across - along);
                    uvs.Add(new Vector2(uLeft, uv.yMin));
                    uvs.Add(new Vector2(uLeft, uv.yMax));
                    uvs.Add(new Vector2(uRight, uv.yMax));
                    uvs.Add(new Vector2(uRight, uv.yMin));
                    for (int corner = 0; corner < 4; corner++)
                    {
                        normals.Add(normal);
                    }

                    triangles.Add(first);
                    triangles.Add(first + 1);
                    triangles.Add(first + 2);
                    triangles.Add(first);
                    triangles.Add(first + 2);
                    triangles.Add(first + 3);
                }

                return this;
            }

            public Mesh ToMesh(string meshName)
            {
                var mesh = new Mesh { name = meshName };
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
