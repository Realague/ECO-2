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

        // A click during a swing is kept this long, and strikes as soon as the swing is over.
        private const float StrikeQueueSeconds = 0.25f;

        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float jumpHeight = 1.25f;
        [SerializeField] private float gravity = 25f;
        [SerializeField] private float mouseSensitivity = 0.1f;

        private WorldSync worldSync;
        private EcosystemSync ecosystemSync;
        private InventorySync inventorySync;
        private HungerSync hungerSync;
        private BowSync bowSync;
        private MiningSync miningSync;
        private LogSync logSync;
        private StorageSync storageSync;
        private StorageScreen storageScreen;
        private HotbarHud hotbar;
        private InventoryScreen inventoryScreen;
        private PlayerModel model;
        private AppearanceMenu appearanceMenu;
        private MapScreen mapScreen;
        private PickupHalo pickupHalo;
        private World world;
        private Transform cameraTransform;
        private Vector3 velocity;
        private bool grounded;
        private float pitch;
        private float faceZoom;
        private bool showsUnderwater;

        // When the last click not yet struck came, or -1.
        private float queuedStrike = -1f;

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
            miningSync = FindAnyObjectByType<MiningSync>();
            logSync = FindAnyObjectByType<LogSync>();
            storageSync = FindAnyObjectByType<StorageSync>();
            storageScreen = FindAnyObjectByType<StorageScreen>();
            hotbar = FindAnyObjectByType<HotbarHud>();
            inventoryScreen = FindAnyObjectByType<InventoryScreen>();
            model = GetComponent<PlayerModel>();
            appearanceMenu = FindAnyObjectByType<AppearanceMenu>();
            mapScreen = FindAnyObjectByType<MapScreen>();
            pickupHalo = FindAnyObjectByType<PickupHalo>();
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
            if ((mapScreen != null && mapScreen.HoldsInput) || (inventoryScreen != null && inventoryScreen.HoldsInput) || StorageScreen.HoldsInput || CommandConsole.HoldsInput)
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
                PreviewStorage();
                HighlightPickup();
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
                || (inventoryScreen != null && inventoryScreen.IsOpen) || StorageScreen.IsOpen) return;

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
                    if (hungerSync.Stomach.IsStarving) hotbar.ShowMessage(InventorySync.TooHungryMessage);
                    else if (inventorySync.Inventory.Has(Items.Arrow)) bowSync.BeginDraw();
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

            // A left click is one hit with the held tool, struck as soon as the last swing is over.
            if (mouse.leftButton.wasPressedThisFrame && held != Items.Bow) queuedStrike = Time.time;
            if (queuedStrike >= 0f && Time.time - queuedStrike > StrikeQueueSeconds) queuedStrike = -1f;

            bool strike = queuedStrike >= 0f && miningSync.CanSwing;
            bool use = mouse.rightButton.wasPressedThisFrame;
            if (!strike && !use) return;

            System.Numerics.Vector3 origin = cameraTransform.position.ToNumerics();
            System.Numerics.Vector3 direction = cameraTransform.forward.ToNumerics();
            bool blockHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, out VoxelHit hit);

            // What is in front of the targeted block counts first: a plant, an animal, a log, an arrow.
            float reachedDistance = blockHit ? hit.Distance : PlayerRules.Reach;
            if (strike)
            {
                // A torch, thin as it is, is struck too (by hand), when the ray meets it first.
                queuedStrike = -1f;
                bool struckHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, true, out VoxelHit struck);
                Strike(held, origin, direction, struckHit, struck, struckHit ? struck.Distance : PlayerRules.Reach);
                return;
            }

            // A right click picks the targeted crop if it is ripe, else picks up the log or arrow
            // pointed at, else eats held food, else uses the held item.
            if (ecosystemSync.TryHarvestAimed(origin, direction, reachedDistance, held, out int missing))
            {
                TellMissing(missing);
                return;
            }

            if (TryPickUp(reachedDistance)) return;

            // Food in hand is eaten, wherever the player looks.
            if (TryEat(held)) return;

            if (!blockHit) return;

            // Nor to till, sow or build: only eating, harvesting and picking up are left to them.
            if (hungerSync.Stomach.IsStarving && (held == Items.Hoe || Items.IsSeed(held) || Items.IsStorage(held) || Items.IsBlock(held)))
            {
                hotbar.ShowMessage(InventorySync.TooHungryMessage);
                return;
            }

            // The server has the last word: the request goes through WorldSync, never through World.SetBlock.
            ushort aimed = world.GetBlock(hit.Block);

            // A right click uses the held item: the hoe tills, a seed is sown on tilled soil, a block is placed.
            if (held == Items.Hoe)
            {
                if (ToolRules.CanTill(aimed) && !world.IsSolid(hit.Block.X, hit.Block.Y + 1, hit.Block.Z)) worldSync.RequestTill(hit.Block);
                return;
            }

            // A chest or a stockpile in hand is placed over the aimed block, facing away from the player.
            if (Items.IsStorage(held) && StorageRules.TryKindOf(held, out StorageKind kind))
            {
                BlockPos cell = hit.Block + hit.Normal;
                int facing = StorageRules.FacingOf(transform.eulerAngles.y);
                if (storageSync.CanPlaceHere(kind, cell, facing, transform.position, out _, out StorageRefusal refusal))
                {
                    storageSync.RequestPlace(kind, cell, facing);
                }
                else
                {
                    hotbar.ShowMessage(StorageSync.TextOf(refusal));
                }

                return;
            }

            // A tree's seed goes straight into its ground, by anyone; a crop's into tilled soil, by a
            // gardener. The server applies the natural rules and says why a seed did not take.
            if (Items.IsSeed(held))
            {
                if (!ecosystemSync.IsTree(Items.SeedSpecies(held)))
                {
                    SowRefusal refusal = inventorySync.Speciality != Speciality.Gardener ? SowRefusal.NotGardener
                        : !EcosystemSync.IsTilled(world, hit.Block.X, hit.Block.Z) ? SowRefusal.NotTilled
                        : SowRefusal.None;
                    if (refusal != SowRefusal.None)
                    {
                        hotbar.ShowMessage(SowRefusals.TextOf(refusal, ""));
                        return;
                    }
                }

                inventorySync.RequestPlant(held, hit.Block.X, hit.Block.Z);
                return;
            }

            if (!Items.IsBlock(held) || !inventorySync.Inventory.Has(held)) return;

            BlockPos target = hit.Block + hit.Normal;

            // A torch stands on the top of the aimed block or hangs on its side, in open air.
            if (held == Blocks.Torch)
            {
                if (!Torches.TryOrient(hit.Normal, out ushort torch))
                {
                    hotbar.ShowMessage("A torch goes on top of a block or on its side");
                    return;
                }

                if (Blocks.TypeOf(world.GetBlock(target)) == Blocks.Air && !storageSync.IsCovered(target)) worldSync.RequestSetBlock(target, torch);
                return;
            }
            bool insidePlayer = VoxelCollision.IntersectsBlock(
                transform.position.ToNumerics(), PlayerRules.HalfWidth, PlayerRules.Height, target);
            if (target != hit.Block && !insidePlayer && !storageSync.IsCovered(target))
            {
                worldSync.RequestSetBlock(target, Items.BlockOf(held));
            }
        }

        // One hit with the held tool on what the player aims at: the plant in front (or animals: the
        // bow), else the block, if it is the right tool (a shovel for soft ground, a pickaxe for anything
        // hard, an axe for wood, a scythe for the rest, a shovel for a stump). With the wrong one, the
        // player is told which; with a tool and nothing to hit, it swings in the air.
        private void Strike(int held, System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, bool blockHit, VoxelHit hit, float reachedDistance)
        {
            // A starving player has no strength left to swing anything.
            if (hungerSync.Stomach.IsStarving)
            {
                if (Items.IsTool(held)) hotbar.ShowMessage(InventorySync.TooHungryMessage);
                return;
            }

            if (ecosystemSync.TryStrike(origin, direction, reachedDistance, held, out int missing, out int plantId, out float distance))
            {
                if (missing != Items.None)
                {
                    TellMissing(missing);
                    return;
                }

                if (miningSync.TryStartSwing(model, false)) miningSync.HitPlant(plantId, (origin + direction * distance).ToUnity());
                return;
            }

            if (blockHit)
            {
                ushort aimed = world.GetBlock(hit.Block);
                if (Blocks.IsBreakable(aimed))
                {
                    // A torch comes down by hand, whatever is held.
                    int tool = ToolRules.ToBreak(aimed);
                    if (tool != Items.None && held != tool)
                    {
                        TellMissing(tool);
                        return;
                    }

                    if (storageSync.StandsOn(hit.Block))
                    {
                        hotbar.ShowMessage("Something stands on it");
                        return;
                    }

                    // A block the player cannot carry stays in place, and the server would refuse it anyway.
                    int drop = Items.DropOf(aimed);
                    if (inventorySync.LocalRoomFor(drop) <= 0)
                    {
                        inventorySync.TellLocalFull(InventorySync.ReasonFor(drop));
                        return;
                    }

                    if (miningSync.TryStartSwing(model, false))
                    {
                        miningSync.HitBlock(hit.Block, aimed, FaceOf(hit.Normal), (origin + direction * hit.Distance).ToUnity());
                    }

                    return;
                }
            }

            if (Items.IsTool(held)) miningSync.TryStartSwing(model, false);
        }

        private enum PickupKind { None, Storage, Log, Arrow, Carcass }

        // What a right click would open or pick up: the storage, log or arrow by its id, a carcass by
        // its column and what is left of it.
        private struct Pickup
        {
            public PickupKind Kind;
            public int Id;
            public int X;
            public int Z;
            public float Meat;
        }

        // The storage, log, arrow or carcass pointed at, the nearest, at most maxDistance away.
        private Pickup AimedPickup(float maxDistance)
        {
            Vector3 origin = cameraTransform.position;
            Vector3 forward = cameraTransform.forward;
            float nearest = maxDistance;
            var pickup = new Pickup();
            if (storageSync.TryAimed(origin, forward, nearest, out int storageId, out float storageDistance))
            {
                nearest = storageDistance;
                pickup = new Pickup { Kind = PickupKind.Storage, Id = storageId };
            }

            if (logSync.TryAimed(origin, forward, nearest, out int logId, out float logDistance))
            {
                nearest = logDistance;
                pickup = new Pickup { Kind = PickupKind.Log, Id = logId };
            }

            if (bowSync.TryAimed(origin, forward, nearest, out int arrowId, out float arrowDistance) && arrowDistance < nearest)
            {
                nearest = arrowDistance;
                pickup = new Pickup { Kind = PickupKind.Arrow, Id = arrowId };
            }

            if (ecosystemSync.TryAimedCarcass(origin, forward, nearest, out int carcassX, out int carcassZ, out _, out float meat, out float carcassDistance) && carcassDistance < nearest)
            {
                pickup = new Pickup { Kind = PickupKind.Carcass, X = carcassX, Z = carcassZ, Meat = meat };
            }

            return pickup;
        }

        // Opens the storage, or picks up the log, arrow or carcass, pointed at: the nearest. Returns
        // false if there is none.
        private bool TryPickUp(float maxDistance)
        {
            Pickup pickup = AimedPickup(maxDistance);
            switch (pickup.Kind)
            {
                case PickupKind.Storage:
                    storageScreen.Open(pickup.Id);
                    return true;
                case PickupKind.Log:
                    logSync.RequestPickup(pickup.Id);
                    return true;
                case PickupKind.Arrow:
                    bowSync.RequestPickup(pickup.Id);
                    return true;
                case PickupKind.Carcass:
                    if (pickup.Meat < EcosystemSync.CarcassMinMeat) hotbar.ShowMessage("Too far gone to take");
                    else ecosystemSync.RequestTakeCarcass(pickup.X, pickup.Z);
                    return true;
                default:
                    return false;
            }
        }

        // A green halo around the arrow or the carcass a right click would pick up (not a carcass too
        // far gone to take), within the same reach and in front of the same block as the click.
        private void HighlightPickup()
        {
            if (pickupHalo == null) return;

            bool blockHit = VoxelRaycast.Cast(world, cameraTransform.position.ToNumerics(), cameraTransform.forward.ToNumerics(), PlayerRules.Reach, out VoxelHit hit);
            Pickup pickup = AimedPickup(blockHit ? hit.Distance : PlayerRules.Reach);
            if (pickup.Kind == PickupKind.Arrow) bowSync.CollectArrowParts(pickup.Id, pickupHalo.Parts);
            else if (pickup.Kind == PickupKind.Carcass && pickup.Meat >= EcosystemSync.CarcassMinMeat) ecosystemSync.CollectCarcassParts(pickup.X, pickup.Z, pickupHalo.Parts);
        }

        // While a chest or a stockpile is in hand, where it would go over the aimed block: green, or
        // red where it cannot.
        private void PreviewStorage()
        {
            int held = hotbar.SelectedItem;
            if (!StorageRules.TryKindOf(held, out StorageKind kind) || world == null) return;

            System.Numerics.Vector3 origin = cameraTransform.position.ToNumerics();
            System.Numerics.Vector3 direction = cameraTransform.forward.ToNumerics();
            if (!VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, out VoxelHit hit)) return;

            int facing = StorageRules.FacingOf(transform.eulerAngles.y);
            bool valid = storageSync.CanPlaceHere(kind, hit.Block + hit.Normal, facing, transform.position, out BlockPos position, out _);
            storageSync.ShowPreview(kind, position, valid);
        }

        // The index of a block face (ChunkMesher.FaceNormals) from its normal.
        private static int FaceOf(BlockPos normal)
        {
            if (normal.X != 0) return normal.X > 0 ? 0 : 1;
            if (normal.Y != 0) return normal.Y > 0 ? 2 : 3;
            return normal.Z > 0 ? 4 : 5;
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
                displacement.ToNumerics(),
                storageSync != null ? storageSync.IsSolid : (System.Func<int, int, int, bool>)null,
                ecosystemSync);

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
