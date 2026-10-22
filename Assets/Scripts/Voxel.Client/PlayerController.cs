using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // First-person player. transform.position is the centre of the feet.
    // Only the owner simulates its player; the other copies follow the NetworkTransform.
    public class PlayerController : NetworkBehaviour
    {
        private const float MaxFallSpeed = 50f;
        private const float MaxDeltaTime = 0.05f;
        private const float MaxPitch = 89f;
        private const float CrosshairSize = 6f;

        // Where the camera stands, in the player's frame, while the character menu shows the player's model.
        // Off to the player's right, so that the model stands clear of the menu on the left of the screen.
        // Close to the face for the eye colour; the camera glides between the two in a quarter of a second.
        private static readonly Vector3 MenuCameraOffset = new Vector3(0.75f, 1.3f, 2.1f);
        private static readonly Vector3 MenuCameraTarget = new Vector3(0.75f, 1.0f, 0f);
        private static readonly Vector3 FaceCameraOffset = new Vector3(0.12f, 1.66f, 0.75f);
        private static readonly Vector3 FaceCameraTarget = new Vector3(0.12f, 1.63f, 0f);
        private const float FaceZoomSpeed = 4f;

        // Swimming: the body counts as in water when the block at this height above the feet is
        // water. There, gravity is weaker, sinking is slow, Space swims up, and moving is slower.
        private const float SwimDepth = 0.4f;
        private const float WaterGravityShare = 0.3f;
        private const float MaxSinkSpeed = 2f;
        private const float SwimUpSpeed = 3f;
        private const float SwimUpAcceleration = 12f;
        private const float SwimSpeedShare = 0.6f;
        private const float UnderwaterFogDensity = 0.09f;
        private static readonly Color UnderwaterFog = new Color(0.10f, 0.28f, 0.42f);
        private const float NightUnderwaterShare = 0.08f;

        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float jumpHeight = 1.25f;
        [SerializeField] private float gravity = 25f;
        [SerializeField] private float mouseSensitivity = 0.1f;

        private WorldSync worldSync;
        private EcosystemSync ecosystemSync;
        private InventorySync inventorySync;
        private HungerSync hungerSync;
        private BowSync bowSync;
        private HotbarHud hotbar;
        private InventoryScreen inventoryScreen;
        private PlayerModel model;
        private AppearanceMenu appearanceMenu;
        private MapScreen mapScreen;
        private World world;
        private Transform cameraTransform;
        private Vector3 velocity;
        private bool grounded;
        private float pitch;
        private float faceZoom;
        private bool showsUnderwater;

        // The last move was stopped by a block on the side.
        private bool againstWall;
        private (bool enabled, Color colour, FogMode mode, float density) airFog;

        private void Awake()
        {
            // Enabled only for the owner, once spawned on the network.
            enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;

            worldSync = FindAnyObjectByType<WorldSync>();
            ecosystemSync = FindAnyObjectByType<EcosystemSync>();
            inventorySync = FindAnyObjectByType<InventorySync>();
            hungerSync = FindAnyObjectByType<HungerSync>();
            bowSync = FindAnyObjectByType<BowSync>();
            hotbar = FindAnyObjectByType<HotbarHud>();
            inventoryScreen = FindAnyObjectByType<InventoryScreen>();
            model = GetComponent<PlayerModel>();
            appearanceMenu = FindAnyObjectByType<AppearanceMenu>();
            mapScreen = FindAnyObjectByType<MapScreen>();
            cameraTransform = Camera.main.transform;
            enabled = true;
        }

        public override void OnNetworkDespawn()
        {
            if (!IsOwner) return;

            enabled = false;
            Cursor.lockState = CursorLockMode.None;
        }

        // Moves the local player at once, as a command asked (CommandSync): the position jumps on the
        // other machines too instead of gliding there, and the player arrives standing still.
        public void Teleport(Vector3 position)
        {
            if (!IsOwner) return;

            velocity = Vector3.zero;
            grounded = false;
            var networkTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
            if (networkTransform != null && networkTransform.CanCommitToTransform)
            {
                networkTransform.Teleport(position, transform.rotation, transform.localScale);
            }
            else
            {
                transform.position = position;
            }
        }

        // Whatever stops the player (leaving the game, despawning), the scene gets its own fog back.
        private void OnDisable()
        {
            ShowUnderwater(false);
        }

        private void Update()
        {
            // On a client, the player spawns before the server has sent the world: we wait.
            world = worldSync.World;
            if (world == null) return;

            // The tool in hand shows in the body's right hand, for everyone.
            model.SetHeldItem(hotbar.SelectedItem);

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            bool hasDevices = keyboard != null && mouse != null;

            // While the character is being created, the player stands still facing the camera.
            if (appearanceMenu != null && appearanceMenu.IsOpen)
            {
                pitch = 0f;
                model.SetLookPitch(pitch);
                Move(keyboard, false);
                PlaceMenuCamera();
                return;
            }

            // The full-screen map, the inventory or the command line has the keyboard: the player stands where they are.
            if ((mapScreen != null && mapScreen.HoldsInput) || (inventoryScreen != null && inventoryScreen.HoldsInput) || CommandConsole.HoldsInput)
            {
                bowSync.CancelDraw();
                Move(keyboard, false);
                PlaceCamera();
                return;
            }

            if (hasDevices && keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            bool hasControl = hasDevices && Cursor.lockState == CursorLockMode.Locked;
            if (hasControl)
            {
                Look(mouse);
            }

            // Without keyboard or mouse, gravity still applies.
            Move(keyboard, hasControl);
            PlaceCamera();

            if (hasControl)
            {
                Interact(mouse);
            }
            else
            {
                bowSync.CancelDraw();
            }

            if (!hasControl && hasDevices && mouse.leftButton.wasPressedThisFrame)
            {
                // The first click captures the mouse, without breaking a block.
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        private void OnGUI()
        {
            if (world == null || (appearanceMenu != null && appearanceMenu.IsOpen) || (mapScreen != null && mapScreen.IsOpen)
                || (inventoryScreen != null && inventoryScreen.IsOpen)) return;

            var crosshair = new Rect(
                (Screen.width - CrosshairSize) / 2f,
                (Screen.height - CrosshairSize) / 2f,
                CrosshairSize,
                CrosshairSize);
            GUI.DrawTexture(crosshair, Texture2D.whiteTexture);
        }

        private void Look(Mouse mouse)
        {
            Vector2 delta = mouse.delta.ReadValue() * mouseSensitivity;
            transform.Rotate(0f, delta.x, 0f);
            pitch = Mathf.Clamp(pitch - delta.y, -MaxPitch, MaxPitch);

            // The other players see the head follow the gaze.
            model.SetLookPitch(pitch);
        }

        // The scene camera follows the local player without being parented to it: it survives the player's despawn.
        // It sits at the eyes of the player's head, which move forward when looking down, so that the
        // player sees its own body from the front.
        private void PlaceCamera()
        {
            cameraTransform.SetPositionAndRotation(
                transform.position + transform.rotation * PlayerModel.EyeOffset(pitch),
                Quaternion.Euler(pitch, transform.eulerAngles.y, 0f));
            ShowUnderwater(IsWaterAt(cameraTransform.position));
        }

        // Under water the view turns blue and short: the scene's fog is swapped for a watery one,
        // and given back when the eyes come out. The water darkens with the day, to a faint glow at night.
        private void ShowUnderwater(bool underwater)
        {
            if (underwater != showsUnderwater)
            {
                showsUnderwater = underwater;
                if (underwater)
                {
                    airFog = (RenderSettings.fog, RenderSettings.fogColor, RenderSettings.fogMode, RenderSettings.fogDensity);
                    RenderSettings.fog = true;
                    RenderSettings.fogMode = FogMode.ExponentialSquared;
                    RenderSettings.fogDensity = UnderwaterFogDensity;
                }
                else
                {
                    (RenderSettings.fog, RenderSettings.fogColor, RenderSettings.fogMode, RenderSettings.fogDensity) = airFog;
                }
            }

            if (underwater) RenderSettings.fogColor = UnderwaterFog * Mathf.Lerp(NightUnderwaterShare, 1f, DayNight.Daylight);
        }

        private bool IsWaterAt(Vector3 position)
        {
            return world.IsWater(Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.y), Mathf.FloorToInt(position.z));
        }

        private void PlaceMenuCamera()
        {
            faceZoom = Mathf.MoveTowards(faceZoom, appearanceMenu.ShowsFace ? 1f : 0f, FaceZoomSpeed * Time.deltaTime);
            float blend = Mathf.SmoothStep(0f, 1f, faceZoom);
            Vector3 position = transform.TransformPoint(Vector3.Lerp(MenuCameraOffset, FaceCameraOffset, blend));
            Vector3 target = transform.TransformPoint(Vector3.Lerp(MenuCameraTarget, FaceCameraTarget, blend));
            cameraTransform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
        }

        // Returns false if the item is not food the player has.
        private bool TryEat(int item)
        {
            if (!hungerSync.TryEat(item, out bool tooFull)) return false;

            if (tooFull) hotbar.ShowMessage("Too full to eat");
            return true;
        }

        // Tells the player which tool they lacked (Items.None: nothing to tell).
        private void TellMissing(int tool)
        {
            if (tool != Items.None) hotbar.ShowMessage(MissingToolMessage(tool));
        }

        internal static string MissingToolMessage(int tool)
        {
            if (tool == Items.Bow) return "You need a bow to hunt";

            string name = Items.NameOf(tool).ToLowerInvariant();
            return ("aeiou".IndexOf(name[0]) >= 0 ? "You need an " : "You need a ") + name;
        }

        private void Interact(Mouse mouse)
        {
            int held = hotbar.SelectedItem;

            // The bow: holding the left button draws it, letting go shoots.
            if (held == Items.Bow)
            {
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    if (inventorySync.Inventory.Has(Items.Arrow)) bowSync.BeginDraw();
                    else hotbar.ShowMessage("No arrows left");
                }
                else if (mouse.leftButton.wasReleasedThisFrame)
                {
                    bowSync.Release(cameraTransform.position, cameraTransform.forward);
                }
            }
            else
            {
                bowSync.CancelDraw();
            }

            bool strike = mouse.leftButton.wasPressedThisFrame && held != Items.Bow;
            bool use = mouse.rightButton.wasPressedThisFrame;
            if (!strike && !use) return;

            System.Numerics.Vector3 origin = cameraTransform.position.ToNumerics();
            System.Numerics.Vector3 direction = cameraTransform.forward.ToNumerics();
            bool blockHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, out VoxelHit hit);

            // A left click strikes the targeted plant (or tells that animals need the bow) if it is in
            // front of the targeted block; a right click picks the targeted crop if it is ripe, else
            // picks up the arrow pointed at.
            float reachedDistance = blockHit ? hit.Distance : PlayerRules.Reach;
            int missing;
            if (strike && ecosystemSync.TryStrike(origin, direction, reachedDistance, held, out missing))
            {
                TellMissing(missing);
                return;
            }

            if (use)
            {
                if (ecosystemSync.TryHarvestAimed(origin, direction, reachedDistance, held, out missing))
                {
                    TellMissing(missing);
                    return;
                }

                if (bowSync.TryPickupAimed(cameraTransform.position, cameraTransform.forward, reachedDistance)) return;

                // Food in hand is eaten, wherever the player looks.
                if (TryEat(held)) return;
            }

            if (!blockHit) return;

            // The server has the last word: the request goes through WorldSync, never through World.SetBlock.
            ushort aimed = world.GetBlock(hit.Block);
            if (strike)
            {
                if (!Blocks.IsBreakable(aimed)) return;

                // A shovel for soft ground, a pickaxe for anything hard.
                int tool = ToolRules.ToBreak(aimed);
                if (held != tool)
                {
                    TellMissing(tool);
                    return;
                }

                // A block the player cannot carry stays in place, and the server would refuse it anyway.
                int drop = Items.DropOf(aimed);
                if (inventorySync.LocalRoomFor(drop) <= 0)
                {
                    inventorySync.TellLocalFull(InventorySync.ReasonFor(drop));
                    return;
                }

                worldSync.RequestSetBlock(hit.Block, Blocks.Air);
                return;
            }

            // A right click uses the held item: the hoe tills, a seed is sown on tilled soil, a block is placed.
            if (held == Items.Hoe)
            {
                if (ToolRules.CanTill(aimed) && !world.IsSolid(hit.Block.X, hit.Block.Y + 1, hit.Block.Z)) worldSync.RequestTill(hit.Block);
                return;
            }

            if (Items.IsSeed(held))
            {
                if (!EcosystemSync.IsTilled(world, hit.Block.X, hit.Block.Z))
                {
                    hotbar.ShowMessage("Seeds only grow on tilled soil: use a hoe");
                    return;
                }

                inventorySync.RequestPlant(held, hit.Block.X, hit.Block.Z);
                return;
            }

            if (!Items.IsBlock(held) || !inventorySync.Inventory.Has(held)) return;

            BlockPos target = hit.Block + hit.Normal;
            bool insidePlayer = VoxelCollision.IntersectsBlock(
                transform.position.ToNumerics(), PlayerRules.HalfWidth, PlayerRules.Height, target);
            if (target != hit.Block && !insidePlayer)
            {
                worldSync.RequestSetBlock(target, Items.BlockOf(held));
            }
        }

        private void Move(Keyboard keyboard, bool hasControl)
        {
            float deltaTime = Mathf.Min(Time.deltaTime, MaxDeltaTime);

            // Input System keys designate a physical position (QWERTY layout):
            // wKey/aKey/sKey/dKey therefore map to Z/Q/S/D on an AZERTY keyboard.
            Vector3 direction = Vector3.zero;
            if (hasControl)
            {
                if (keyboard.wKey.isPressed) direction += transform.forward;
                if (keyboard.sKey.isPressed) direction -= transform.forward;
                if (keyboard.dKey.isPressed) direction += transform.right;
                if (keyboard.aKey.isPressed) direction -= transform.right;
            }

            bool swimming = IsWaterAt(transform.position + Vector3.up * SwimDepth);
            bool spacePressed = hasControl && keyboard.spaceKey.isPressed;

            // A starving player walks slower and jumps just high enough to climb a block.
            bool starving = hungerSync.Stomach.IsStarving;
            float speed = starving ? moveSpeed * HungerRules.StarvingSpeedShare : moveSpeed;
            float jump = starving ? Mathf.Min(jumpHeight, HungerRules.StarvingJumpHeight) : jumpHeight;

            Vector3 horizontal = direction.normalized * (swimming ? speed * SwimSpeedShare : speed);
            velocity.x = horizontal.x;
            velocity.z = horizontal.z;

            if (!swimming && grounded && spacePressed)
            {
                velocity.y = Mathf.Sqrt(2f * gravity * jump);
            }

            // Swimming against a bank, Space gives the push of a jump: enough to climb onto ground
            // as high as the water's surface, which swimming up alone never reaches.
            bool climbingOut = swimming && spacePressed && againstWall && direction != Vector3.zero;
            if (climbingOut) velocity.y = Mathf.Max(velocity.y, Mathf.Sqrt(2f * gravity * jump));

            // Vertical move with the average of the speeds before and after gravity: exact for a
            // constant acceleration, so jump height does not depend on the frame rate.
            float nextVerticalSpeed;
            if (!swimming || climbingOut) nextVerticalSpeed = Mathf.Max(velocity.y - gravity * deltaTime, -MaxFallSpeed);
            else if (spacePressed) nextVerticalSpeed = Mathf.MoveTowards(velocity.y, SwimUpSpeed, SwimUpAcceleration * deltaTime);
            else nextVerticalSpeed = Mathf.Max(velocity.y - gravity * WaterGravityShare * deltaTime, -MaxSinkSpeed);

            Vector3 displacement = velocity * deltaTime;
            displacement.y = (velocity.y + nextVerticalSpeed) * 0.5f * deltaTime;
            velocity.y = nextVerticalSpeed;

            MoveResult result = VoxelCollision.Move(
                world,
                transform.position.ToNumerics(),
                PlayerRules.HalfWidth,
                PlayerRules.Height,
                displacement.ToNumerics());

            transform.position = result.Position.ToUnity();
            grounded = result.Grounded;
            againstWall = result.HitWall;

            if ((grounded && velocity.y < 0f) || (result.HitCeiling && velocity.y > 0f))
            {
                velocity.y = 0f;
            }
        }
    }
}
