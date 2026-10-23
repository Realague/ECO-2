using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;
using Tile = Voxel.Client.PlayerSkin.Tile;

namespace Voxel.Client
{
    // Block-style body of a player, built from boxes at runtime, and its animation. Each joint (hips,
    // spine, neck, shoulders, elbows, thighs, knees) is a pivot carrying one mesh made of the boxes it
    // moves. The animation is driven by how the player object moves, so it works the same for the
    // owner and for the copies that follow the NetworkTransform. The only thing sent for it is the
    // pitch of the gaze (the NetworkTransform already carries the yaw), which the head follows, and
    // the tool in hand, drawn in the right fist (ToolShapes).
    // The owner's camera sits at the eyes of this head (EyeOffset), so the owner sees its own body
    // when looking down; only its head is hidden, keeping its shadow.
    //
    // The body is rebuilt from the player's Appearance (skin tone, sex, hair style), which the server
    // sets from the player's saved state and sends to everyone. Until the player has chosen one, the
    // default appearance is shown, and the owner can preview choices locally.
    public class PlayerModel : NetworkBehaviour
    {
        // Heights in blocks: feet at 0, eyes near PlayerRules.EyeHeight (1.62), top of the hair at 1.82.
        private const float HipHeight = 0.86f;
        private const float ThighLength = 0.42f;
        private const float ShinLength = 0.36f;
        private const float PelvisHeight = 0.12f;
        private const float ChestHeight = 0.46f;
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

        // Where the body reads the sky light, above the feet, and how fast its shade follows.
        private const float ChestLightHeight = 1.2f;
        private const float ShadeSmoothing = 6f;

        // A tool in the right hand points forward from the fist, scaled down from ToolShapes' size, the
        // forearm raised to hold it in front.
        private const float ToolScale = 0.7f;
        private const float HoldingElbowBend = 45f;
        private const float HoldingArmRaise = 15f;
        private const float ToolTilt = 110f;
        private const float ShovelGripHeight = 1.05f;
        private static readonly Vector3 HandInForearm = new Vector3(0f, -0.3f, 0.005f);

        [SerializeField] private Material material;

        [Header("Tools in hand (wood, iron, string and feathers)")]
        [SerializeField] private Material toolWood;
        [SerializeField] private Material toolIron;
        [SerializeField] private Material toolLight;

        // The tool the owner holds (Items.Shovel...), or Items.None: what everyone sees in its right hand.
        private readonly NetworkVariable<int> heldTool = new NetworkVariable<int>(
            Items.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // The owner's swings so far (wrapping round): each new one plays a swing of the right arm and
        // its tool on every machine.
        private readonly NetworkVariable<byte> swingCount = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // A swing: the arm chops down to the hit (Impact), then comes back up past where it holds the
        // tool and settles (MiningRules.SwingSeconds in all).
        private const float SwingImpactSeconds = 0.06f;
        private const float SwingChopDegrees = 30f;
        private const float SwingRaiseDegrees = 60f;
        private const float SwingElbowDegrees = 25f;

        // One mesh per tool, shared by every player: a submesh per material, the grip at the origin.
        private static readonly Dictionary<int, Mesh> ToolMeshes = new Dictionary<int, Mesh>();

        // Pitch of the owner's gaze in whole degrees, positive downwards: one byte, sent only when it changes.
        private readonly NetworkVariable<sbyte> lookPitch = new NetworkVariable<sbyte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // The player's chosen appearance as Appearance.Encode(), or Appearance.NotChosen. Only the server writes it.
        private readonly NetworkVariable<ushort> appearanceCode = new NetworkVariable<ushort>(
            Appearance.NotChosen, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // The name the player chose (PlayerNames), empty until then. Only the server writes it.
        private readonly NetworkVariable<FixedString64Bytes> playerName = new NetworkVariable<FixedString64Bytes>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

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
        private Appearance shown;
        private Color shirt;
        private bool headHidden;
        private float previewYaw;

        private Vector3 lastPosition;
        private float speed;
        private float verticalSpeed;
        private float phase;
        private float pitch;
        private float ownPitch;
        private float skyShade = 1f;
        private int ownTool;
        private int toolShown = -1;
        private GameObject toolObject;
        private float swingStart = -10f;
        private byte swingsSeen;

        private static VoxelWorld voxelWorld;

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

        // Called by the owner's controller with the item in its hand: a tool shows in the right hand
        // of its body, on every machine (one int, sent only when it changes); anything else shows nothing.
        public void SetHeldItem(int item)
        {
            ownTool = Items.IsTool(item) ? item : Items.None;
            if (IsSpawned && IsOwner && heldTool.Value != ownTool) heldTool.Value = ownTool;
        }

        // Called by the owner's controller when it swings its tool (MiningSync): the swing plays here at
        // once and on the other machines when the count reaches them.
        public void Swing()
        {
            swingStart = Time.time;
            if (IsSpawned && IsOwner) swingCount.Value = (byte)(swingCount.Value + 1);
        }

        // True once the player has chosen an appearance (it is then the same on every machine).
        public bool HasChosenAppearance => IsSpawned && appearanceCode.Value != Appearance.NotChosen;

        // The name the player chose, the same on every machine; empty until they have chosen one.
        public string PlayerName => IsSpawned ? playerName.Value.ToString() : "";

        public bool HasChosenName => PlayerName.Length > 0;

        // Server side: the name kept in the player's saved state, sent to every machine.
        internal void SetPlayerName(string name)
        {
            if (IsServer && playerName.Value.ToString() != name) playerName.Value = new FixedString64Bytes(name);
        }

        // The appearance currently displayed: the chosen one, a preview, or the default.
        public Appearance Appearance => shown;

        // Shows an appearance on this machine only, while the owner is choosing.
        public void Preview(Appearance appearance)
        {
            if (appearance.IsValid) Show(appearance);
        }

        // Server side: the appearance kept in the player's saved state, sent to every machine.
        internal void SetAppearanceCode(ushort code)
        {
            if (IsServer && appearanceCode.Value != code) appearanceCode.Value = code;
        }

        // The owner's camera is inside the head, which is hidden then; the character menu shows it.
        public void SetHeadVisible(bool visible)
        {
            headHidden = !visible;
            head.GetComponent<MeshRenderer>().shadowCastingMode = visible ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly;
        }

        // Turns the body on this machine only, about its vertical axis, so that the character menu can
        // show it from every side. The player object itself, and what others see, do not turn.
        public void SetPreviewYaw(float degrees)
        {
            previewYaw = degrees;
            if (model != null) model.localRotation = Quaternion.Euler(0f, previewYaw, 0f);
        }

        private void Awake()
        {
            shirt = PlayerSkin.ShirtFor(0);
            Show(Appearance.Default);
            lastPosition = transform.position;
        }

        public override void OnNetworkSpawn()
        {
            // Set before the object reaches the clients, so that they never see the default first.
            if (IsServer)
            {
                AppearanceSync appearanceSync = FindAnyObjectByType<AppearanceSync>();
                if (appearanceSync != null)
                {
                    appearanceCode.Value = appearanceSync.StoredCode(OwnerClientId);
                    playerName.Value = new FixedString64Bytes(appearanceSync.StoredName(OwnerClientId));
                }
            }

            appearanceCode.OnValueChanged += OnAppearanceChanged;
            shirt = PlayerSkin.ShirtFor(OwnerClientId);
            ShowCode(appearanceCode.Value, true);
            if (IsOwner) SetHeadVisible(false);
            lastPosition = transform.position;
            swingsSeen = swingCount.Value;
        }

        public override void OnNetworkDespawn()
        {
            appearanceCode.OnValueChanged -= OnAppearanceChanged;
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            DestroyBody();
            if (skinMaterial != null) Destroy(skinMaterial);
            if (skinTexture != null) Destroy(skinTexture);
        }

        private void OnAppearanceChanged(ushort previous, ushort current) => ShowCode(current, false);

        private void ShowCode(ushort code, bool force)
        {
            Show(Appearance.TryDecode(code, out Appearance appearance) ? appearance : Appearance.Default, force);
        }

        // Rebuilds the body and its texture for an appearance; the joints keep no state worth saving.
        private void Show(Appearance appearance, bool force = false)
        {
            if (!force && model != null && appearance.Equals(shown)) return;

            shown = appearance;
            DestroyBody();
            Build(appearance);
            Skin(appearance);
            if (headHidden) SetHeadVisible(false);
        }

        private void DestroyBody()
        {
            foreach (Mesh mesh in meshes)
            {
                Destroy(mesh);
            }

            meshes.Clear();
            renderers.Clear();
            if (model != null) Destroy(model.gameObject);
            model = null;

            // The tool went with the hand: it is put back by the next LateUpdate.
            toolObject = null;
            toolShown = -1;
        }

        // Puts a tool in the right hand (Items.None: empties it).
        private void ShowTool(int tool)
        {
            toolShown = tool;
            if (toolObject != null) Destroy(toolObject);
            toolObject = null;
            if (tool == Items.None || rightForearm == null || toolWood == null) return;

            toolObject = new GameObject("Tool");
            Transform held = toolObject.transform;
            held.SetParent(rightForearm, false);
            held.localPosition = HandInForearm;
            // The raised forearm points forward and up: tipped past its axis, the tool points ahead and up, about 40°.
            held.localRotation = Quaternion.Euler(ToolTilt, 0f, 0f) * Quaternion.Euler(0f, TurnInHand(tool), 0f);
            held.localScale = Vector3.one * ToolScale;
            toolObject.AddComponent<MeshFilter>().sharedMesh = ToolMesh(tool);
            toolObject.AddComponent<MeshRenderer>().sharedMaterials = new[] { toolWood, toolIron, toolLight };
        }

        // The turn about the handle that brings a tool's head into the plane of its swing: the axe's blade
        // (ToolShapes' +x) faces down and ahead, the bow's string (-x) faces the player.
        private static float TurnInHand(int tool)
        {
            return tool == Items.Axe || tool == Items.Pickaxe || tool == Items.Bow ? -90f : 0f;
        }

        private static Mesh ToolMesh(int tool)
        {
            if (ToolMeshes.TryGetValue(tool, out Mesh mesh) && mesh != null) return mesh;

            // A shovel is held near the top of its handle, blade ahead: turned end over end about the grip.
            bool byTheTop = tool == Items.Shovel;
            float grip = byTheTop ? ShovelGripHeight : ToolShapes.GripHeight;
            var submeshes = new List<(Vector3, Vector3)>[] { new List<(Vector3, Vector3)>(), new List<(Vector3, Vector3)>(), new List<(Vector3, Vector3)>() };
            foreach (ToolShapes.Box box in ToolShapes.Of(tool))
            {
                Vector3 centre = box.Centre - new Vector3(0f, grip, 0f);
                if (byTheTop) centre = new Vector3(-centre.x, -centre.y, centre.z);
                submeshes[(int)box.Stuff].Add((centre, box.Size));
            }

            mesh = BoxMesh.Build("Tool " + Items.NameOf(tool), submeshes);
            ToolMeshes[tool] = mesh;
            return mesh;
        }

        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            Vector3 position = transform.position;
            ShadeForTheSky(position, deltaTime);
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

            int tool = IsSpawned && !IsOwner ? heldTool.Value : ownTool;
            if (tool != toolShown) ShowTool(tool);

            // Another player swung: their arm swings here too.
            if (IsSpawned && !IsOwner && swingCount.Value != swingsSeen)
            {
                swingsSeen = swingCount.Value;
                swingStart = Time.time;
            }

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
            // A tool in the right hand: that arm swings half as much and holds it in front.
            float holding = toolObject != null ? 1f : 0f;
            float rightSwing = swing * (1f - 0.5f * holding);
            SwingPose(time - swingStart, holding, out float chop, out float straighten);
            rightUpperArm.localRotation = Quaternion.Euler(rightSwing * ArmSwing - airborne * 35f - holding * HoldingArmRaise + chop, 0f, sway + airborne * 40f);
            leftForearm.localRotation = Quaternion.Euler(-(10f + Mathf.Max(0f, swing) * ElbowBend + airborne * 25f), 0f, 0f);
            rightForearm.localRotation = Quaternion.Euler(-(10f + holding * (HoldingElbowBend - 10f) + Mathf.Max(0f, -rightSwing) * ElbowBend + airborne * 25f) + straighten, 0f, 0f);
        }

        // A tool's swing, elapsed seconds after it started: the arm chops down to the hit, the forearm
        // straightening, then comes back up past where it holds the tool and settles. Nothing without
        // a tool in hand or once the swing is over.
        private static void SwingPose(float elapsed, float holding, out float chop, out float straighten)
        {
            chop = 0f;
            straighten = 0f;
            if (holding <= 0f || elapsed < 0f || elapsed >= MiningRules.SwingSeconds) return;

            if (elapsed < SwingImpactSeconds)
            {
                float u = elapsed / SwingImpactSeconds;
                chop = SwingChopDegrees * u;
                straighten = SwingElbowDegrees * u;
                return;
            }

            float back = (elapsed - SwingImpactSeconds) / (MiningRules.SwingSeconds - SwingImpactSeconds);
            chop = SwingChopDegrees * (1f - back) - SwingRaiseDegrees * Mathf.Sin(Mathf.PI * back);
            straighten = SwingElbowDegrees * (1f - back);
        }

        // Out of the sky's reach (in a tunnel, under a roof) the body darkens like the blocks around
        // it, by the sky light at its chest, eased so that crossing from one block to the next does not show.
        private void ShadeForTheSky(Vector3 feet, float deltaTime)
        {
            if (skinMaterial == null) return;

            if (voxelWorld == null) voxelWorld = FindAnyObjectByType<VoxelWorld>();
            float target = voxelWorld != null ? voxelWorld.SkyBrightnessAt(feet + Vector3.up * ChestLightHeight) : 1f;
            skyShade = Mathf.Lerp(skyShade, target, 1f - Mathf.Exp(-ShadeSmoothing * deltaTime));

            Color colour = material.color * skyShade;
            colour.a = material.color.a;
            skinMaterial.color = colour;
        }

        private void Skin(Appearance appearance)
        {
            if (skinTexture != null) Destroy(skinTexture);
            skinTexture = PlayerSkin.CreateTexture(shirt, appearance);
            if (skinMaterial == null) skinMaterial = new Material(material) { name = "PlayerSkin" };
            skinMaterial.mainTexture = skinTexture;
            foreach (MeshRenderer part in renderers)
            {
                part.sharedMaterial = skinMaterial;
            }
        }

        // Positions are relative to the joint that carries each box. +X is the player's right, +Z its front.
        // A woman's body is a little narrower at the shoulders and has slimmer limbs.
        private void Build(Appearance appearance)
        {
            bool female = appearance.Sex == Sex.Female;
            var body = new BodySize(female);

            model = new GameObject("Model").transform;
            model.SetParent(transform, false);
            model.localRotation = Quaternion.Euler(0f, previewYaw, 0f);

            hips = Joint("Hips", model, new Vector3(0f, HipHeight, 0f), new BoxBuilder()
                .Add(new Vector3(0f, PelvisHeight / 2f, 0f), new Vector3(body.PelvisWidth, PelvisHeight, body.ChestDepth),
                    new Faces(Tile.BeltFront, Tile.Belt, Tile.Belt, Tile.Trousers, Tile.Trousers)));

            leftThigh = Thigh("LeftThigh", -body.LegX, body);
            rightThigh = Thigh("RightThigh", body.LegX, body);
            leftShin = Shin("LeftShin", leftThigh, body);
            rightShin = Shin("RightShin", rightThigh, body);

            float chestTop = ChestHeight;
            float strapX = body.ChestWidth / 2f - 0.07f;
            float chestFront = body.ChestDepth / 2f;
            spine = Joint("Spine", hips, new Vector3(0f, PelvisHeight, 0f), new BoxBuilder()
                .Add(new Vector3(0f, ChestHeight / 2f, 0f), new Vector3(body.ChestWidth, ChestHeight, body.ChestDepth),
                    new Faces(Tile.ChestFront, Tile.ChestBack, Tile.ChestSide, Tile.Shirt, Tile.Shirt))
                .Add(new Vector3(-strapX, 0.25f, chestFront + 0.006f), new Vector3(0.045f, 0.40f, 0.012f), Faces.All(Tile.Strap))
                .Add(new Vector3(strapX, 0.25f, chestFront + 0.006f), new Vector3(0.045f, 0.40f, 0.012f), Faces.All(Tile.Strap))
                .Add(new Vector3(-strapX, chestTop + 0.006f, 0f), new Vector3(0.045f, 0.012f, body.ChestDepth + 0.024f), Faces.All(Tile.Strap))
                .Add(new Vector3(strapX, chestTop + 0.006f, 0f), new Vector3(0.045f, 0.012f, body.ChestDepth + 0.024f), Faces.All(Tile.Strap))
                .Add(new Vector3(0f, 0.22f, -chestFront - 0.07f), new Vector3(0.32f, 0.36f, 0.14f),
                    new Faces(Tile.Canvas, Tile.PackBack, Tile.PackSide, Tile.PackTop, Tile.Canvas)));

            // The neck belongs to the head, which the owner does not see: it would sit right under its eyes.
            var headBoxes = new BoxBuilder()
                .Add(new Vector3(0f, 0.02f, 0f), new Vector3(0.13f, 0.06f, 0.13f), Faces.All(Tile.SkinShadow))
                .Add(new Vector3(0f, 0.03f + HeadSize / 2f, 0f), Vector3.one * HeadSize,
                    new Faces(Tile.Face, Tile.HeadBack, Tile.HeadSide, Tile.Hair, Tile.SkinShadow))
                .Add(new Vector3(0f, 0.166f, 0.165f), new Vector3(0.045f, 0.04f, 0.03f), Faces.All(Tile.Skin))
                .Add(new Vector3(-0.16f, 0.17f, -0.01f), new Vector3(0.025f, 0.07f, 0.05f), Faces.All(Tile.Skin))
                .Add(new Vector3(0.16f, 0.17f, -0.01f), new Vector3(0.025f, 0.07f, 0.05f), Faces.All(Tile.Skin));
            AddHair(headBoxes, appearance.Hair);
            AddFacialHair(headBoxes, appearance.FacialHair);
            head = Joint("Head", spine, new Vector3(0f, chestTop, 0f), headBoxes);

            float shoulderX = body.ChestWidth / 2f + body.ArmWidth / 2f;
            leftUpperArm = UpperArm("LeftUpperArm", -shoulderX, body);
            rightUpperArm = UpperArm("RightUpperArm", shoulderX, body);
            leftForearm = Forearm("LeftForearm", leftUpperArm, body);
            rightForearm = Forearm("RightForearm", rightUpperArm, body);
        }

        // Hair volumes on top of the head, relative to the neck joint (the head spans 0.03 to 0.33).
        // A buzz cut is mostly painted on; a bald head has none.
        private static void AddHair(BoxBuilder boxes, HairStyle style)
        {
            var cap = new Faces(Tile.HairEdge, Tile.HairEdge, Tile.HairEdge, Tile.Hair, Tile.Hair);
            var hair = Faces.All(Tile.HairEdge);
            switch (style)
            {
                case HairStyle.Bald:
                    return;
                case HairStyle.Buzz:
                    boxes.Add(new Vector3(0f, 0.34f, 0f), new Vector3(0.31f, 0.02f, 0.31f), cap);
                    return;
            }

            boxes.Add(new Vector3(0f, 0.3575f, -0.005f), new Vector3(0.32f, 0.055f, 0.32f), cap);
            boxes.Add(new Vector3(0f, 0.305f, 0.155f), new Vector3(0.3f, 0.05f, 0.02f), hair);
            switch (style)
            {
                case HairStyle.Long:
                    // Down past the neck over the back, with locks over the ears.
                    boxes.Add(new Vector3(0f, 0.15f, -0.165f), new Vector3(0.33f, 0.40f, 0.04f), hair);
                    boxes.Add(new Vector3(-0.165f, 0.2f, -0.05f), new Vector3(0.03f, 0.24f, 0.2f), hair);
                    boxes.Add(new Vector3(0.165f, 0.2f, -0.05f), new Vector3(0.03f, 0.24f, 0.2f), hair);
                    break;
                case HairStyle.Ponytail:
                    boxes.Add(new Vector3(0f, 0.24f, -0.16f), new Vector3(0.32f, 0.2f, 0.03f), hair);
                    boxes.Add(new Vector3(0f, 0.27f, -0.19f), new Vector3(0.07f, 0.05f, 0.05f), Faces.All(Tile.Strap));
                    boxes.Add(new Vector3(0f, 0.12f, -0.205f), new Vector3(0.08f, 0.26f, 0.07f), hair);
                    break;
                case HairStyle.Bun:
                    boxes.Add(new Vector3(0f, 0.24f, -0.16f), new Vector3(0.32f, 0.2f, 0.03f), hair);
                    boxes.Add(new Vector3(0f, 0.33f, -0.17f), new Vector3(0.15f, 0.13f, 0.13f), cap);
                    break;
                default:
                    boxes.Add(new Vector3(0f, 0.24f, -0.16f), new Vector3(0.32f, 0.2f, 0.03f), hair);
                    break;
            }
        }

        // Facial hair volumes in front of the face, relative to the neck joint: the mouth spans 0.096 to
        // 0.111 and the nose starts at 0.146. Stubble is only painted on.
        private static void AddFacialHair(BoxBuilder boxes, FacialHair facialHair)
        {
            if (facialHair != FacialHair.Moustache && facialHair != FacialHair.Beard) return;

            var hair = Faces.All(Tile.HairEdge);
            boxes.Add(new Vector3(0f, 0.121f, 0.156f), new Vector3(0.13f, 0.022f, 0.012f), hair);

            // Under the mouth to below the chin, across the whole jaw.
            if (facialHair == FacialHair.Beard) boxes.Add(new Vector3(0f, 0.045f, 0.16f), new Vector3(0.3f, 0.09f, 0.03f), hair);
        }

        // Proportions that depend on the body type.
        private readonly struct BodySize
        {
            public readonly float ChestWidth;
            public readonly float ChestDepth;
            public readonly float PelvisWidth;
            public readonly float LegX;
            public readonly float LegWidth;
            public readonly float ArmWidth;
            public readonly float ForearmWidth;
            public readonly float HandWidth;

            public BodySize(bool female)
            {
                ChestWidth = female ? 0.4f : 0.44f;
                ChestDepth = female ? 0.22f : 0.24f;
                PelvisWidth = female ? 0.38f : 0.4f;
                LegX = female ? 0.095f : 0.1f;
                LegWidth = female ? 0.16f : 0.17f;
                ArmWidth = female ? 0.12f : 0.13f;
                ForearmWidth = female ? 0.1f : 0.11f;
                HandWidth = female ? 0.11f : 0.12f;
            }
        }

        private Transform Thigh(string jointName, float x, BodySize body)
        {
            return Joint(jointName, hips, new Vector3(x, 0f, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -ThighLength / 2f, 0f), new Vector3(body.LegWidth, ThighLength, 0.18f),
                    new Faces(Tile.TrouserFront, Tile.Trousers, Tile.TrouserSide, Tile.Trousers, Tile.Trousers)));
        }

        // The shin carries the shoe, which sticks out forward.
        private Transform Shin(string jointName, Transform thigh, BodySize body)
        {
            return Joint(jointName, thigh, new Vector3(0f, -ThighLength, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -ShinLength / 2f, 0f), new Vector3(body.LegWidth - 0.02f, ShinLength, 0.16f),
                    new Faces(Tile.ShinFront, Tile.ShinFront, Tile.ShinSide, Tile.Trousers, Tile.Trousers))
                .Add(new Vector3(0f, -ShinLength - 0.04f, 0.035f), new Vector3(0.16f, 0.08f, 0.27f),
                    new Faces(Tile.ShoeFront, Tile.ShoeFront, Tile.ShoeSide, Tile.ShoeTop, Tile.Sole)));
        }

        private Transform UpperArm(string jointName, float x, BodySize body)
        {
            return Joint(jointName, spine, new Vector3(x, ChestHeight - 0.04f, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -0.13f, 0f), new Vector3(body.ArmWidth, 0.32f, body.ArmWidth),
                    new Faces(Tile.Sleeve, Tile.Sleeve, Tile.Sleeve, Tile.Shirt, Tile.Skin)));
        }

        // The forearm carries the hand.
        private Transform Forearm(string jointName, Transform upperArm, BodySize body)
        {
            return Joint(jointName, upperArm, new Vector3(0f, -UpperArmLength, 0f), new BoxBuilder()
                .Add(new Vector3(0f, -0.12f, 0f), new Vector3(body.ForearmWidth, 0.25f, body.ForearmWidth),
                    new Faces(Tile.Forearm, Tile.Forearm, Tile.Forearm, Tile.Skin, Tile.Skin))
                .Add(new Vector3(0f, -0.3f, 0.005f), new Vector3(body.HandWidth, 0.11f, 0.085f),
                    new Faces(Tile.Hand, Tile.Hand, Tile.Hand, Tile.Hand, Tile.Fingers)));
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
