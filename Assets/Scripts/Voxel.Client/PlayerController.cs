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

        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float jumpHeight = 1.25f;
        [SerializeField] private float gravity = 25f;
        [SerializeField] private float mouseSensitivity = 0.1f;

        private WorldSync worldSync;
        private EcosystemSync ecosystemSync;
        private InventorySync inventorySync;
        private InventoryHud inventoryHud;
        private PlayerModel model;
        private AppearanceMenu appearanceMenu;
        private MapScreen mapScreen;
        private World world;
        private Transform cameraTransform;
        private Vector3 velocity;
        private bool grounded;
        private float pitch;
        private float faceZoom;

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
            inventoryHud = FindAnyObjectByType<InventoryHud>();
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

        private void Update()
        {
            // On a client, the player spawns before the server has sent the world: we wait.
            world = worldSync.World;
            if (world == null) return;

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

            // The full-screen map has the mouse and the keyboard: the player stands where they are.
            if (mapScreen != null && mapScreen.HoldsInput)
            {
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
            else if (hasDevices && mouse.leftButton.wasPressedThisFrame)
            {
                // The first click captures the mouse, without breaking a block.
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        private void OnGUI()
        {
            if (world == null || (appearanceMenu != null && appearanceMenu.IsOpen) || (mapScreen != null && mapScreen.IsOpen)) return;

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
        }

        private void PlaceMenuCamera()
        {
            faceZoom = Mathf.MoveTowards(faceZoom, appearanceMenu.ShowsFace ? 1f : 0f, FaceZoomSpeed * Time.deltaTime);
            float blend = Mathf.SmoothStep(0f, 1f, faceZoom);
            Vector3 position = transform.TransformPoint(Vector3.Lerp(MenuCameraOffset, FaceCameraOffset, blend));
            Vector3 target = transform.TransformPoint(Vector3.Lerp(MenuCameraTarget, FaceCameraTarget, blend));
            cameraTransform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
        }

        private void Interact(Mouse mouse)
        {
            bool breakBlock = mouse.leftButton.wasPressedThisFrame;
            bool placeBlock = mouse.rightButton.wasPressedThisFrame;
            if (!breakBlock && !placeBlock) return;

            System.Numerics.Vector3 origin = cameraTransform.position.ToNumerics();
            System.Numerics.Vector3 direction = cameraTransform.forward.ToNumerics();
            bool blockHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, out VoxelHit hit);

            // A left click strikes the targeted animal or plant if it is in front of the targeted block.
            if (breakBlock)
            {
                float blockDistance = blockHit ? hit.Distance : PlayerRules.Reach;
                if (ecosystemSync.TryStrike(origin, direction, blockDistance)) return;
            }

            if (!blockHit) return;

            // The server has the last word: the request goes through WorldSync, never through World.SetBlock.
            if (breakBlock)
            {
                worldSync.RequestSetBlock(hit.Block, Blocks.Air);
                return;
            }

            // A right click uses the held item: a seed is planted in the targeted column, a block is placed.
            int held = inventoryHud.SelectedItem;
            if (Items.IsSeed(held))
            {
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

            Vector3 horizontal = direction.normalized * moveSpeed;
            velocity.x = horizontal.x;
            velocity.z = horizontal.z;

            if (grounded && hasControl && keyboard.spaceKey.isPressed)
            {
                velocity.y = Mathf.Sqrt(2f * gravity * jumpHeight);
            }

            // Vertical move with the average of the speeds before and after gravity: exact for a
            // constant acceleration, so jump height does not depend on the frame rate.
            float nextVerticalSpeed = Mathf.Max(velocity.y - gravity * deltaTime, -MaxFallSpeed);
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

            if ((grounded && velocity.y < 0f) || (result.HitCeiling && velocity.y > 0f))
            {
                velocity.y = 0f;
            }
        }
    }
}
