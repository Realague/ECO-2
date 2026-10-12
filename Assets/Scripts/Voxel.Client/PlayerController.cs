using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Joueur à la première personne. transform.position est le centre des pieds.
    public class PlayerController : MonoBehaviour
    {
        private const float HalfWidth = 0.3f;
        private const float Height = 1.8f;
        private const float MaxFallSpeed = 50f;
        private const float MaxDeltaTime = 0.05f;
        private const float MaxPitch = 89f;
        private const float CrosshairSize = 6f;

        [SerializeField] private VoxelWorld voxelWorld;
        [SerializeField] private Transform cameraTransform;
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float jumpHeight = 1.25f;
        [SerializeField] private float gravity = 25f;
        [SerializeField] private float mouseSensitivity = 0.1f;
        [SerializeField] private float reach = 5f;

        private World world;
        private Vector3 velocity;
        private bool grounded;
        private float pitch;
        private ushort selectedBlock = Blocks.Dirt;

        private void Start()
        {
            world = voxelWorld.World;

            int x = world.SizeX / 2;
            int z = world.SizeZ / 2;
            transform.position = new Vector3(x + 0.5f, world.GetSurfaceHeight(x, z), z + 0.5f);
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (keyboard == null || mouse == null)
            {
                // Sans clavier ni souris, la gravité s'applique quand même.
                Move(keyboard, false);
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            bool hasControl = Cursor.lockState == CursorLockMode.Locked;
            if (hasControl)
            {
                Look(mouse);
                SelectBlock(keyboard);
                Interact(mouse);
            }
            else if (mouse.leftButton.wasPressedThisFrame)
            {
                // Le premier clic capture la souris, sans casser de bloc.
                Cursor.lockState = CursorLockMode.Locked;
            }

            Move(keyboard, hasControl);
        }

        private void OnGUI()
        {
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
            cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void SelectBlock(Keyboard keyboard)
        {
            if (keyboard.digit1Key.wasPressedThisFrame) selectedBlock = Blocks.Stone;
            if (keyboard.digit2Key.wasPressedThisFrame) selectedBlock = Blocks.Dirt;
            if (keyboard.digit3Key.wasPressedThisFrame) selectedBlock = Blocks.Grass;
        }

        private void Interact(Mouse mouse)
        {
            bool breakBlock = mouse.leftButton.wasPressedThisFrame;
            bool placeBlock = mouse.rightButton.wasPressedThisFrame;
            if (!breakBlock && !placeBlock) return;

            System.Numerics.Vector3 origin = cameraTransform.position.ToNumerics();
            System.Numerics.Vector3 direction = cameraTransform.forward.ToNumerics();
            if (!VoxelRaycast.Cast(world, origin, direction, reach, out VoxelHit hit)) return;

            if (breakBlock)
            {
                world.SetBlock(hit.Block, Blocks.Air);
                return;
            }

            BlockPos target = hit.Block + hit.Normal;
            bool insidePlayer = VoxelCollision.IntersectsBlock(transform.position.ToNumerics(), HalfWidth, Height, target);
            if (target != hit.Block && !insidePlayer)
            {
                world.SetBlock(target, selectedBlock);
            }
        }

        private void Move(Keyboard keyboard, bool hasControl)
        {
            float deltaTime = Mathf.Min(Time.deltaTime, MaxDeltaTime);

            // Les touches de l'Input System désignent une position physique (disposition QWERTY) :
            // wKey/aKey/sKey/dKey correspondent donc à Z/Q/S/D sur un clavier AZERTY.
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

            // Déplacement vertical avec la moyenne des vitesses avant et après gravité : exact pour une
            // accélération constante, donc la hauteur de saut ne dépend pas de la fréquence d'images.
            float nextVerticalSpeed = Mathf.Max(velocity.y - gravity * deltaTime, -MaxFallSpeed);
            Vector3 displacement = velocity * deltaTime;
            displacement.y = (velocity.y + nextVerticalSpeed) * 0.5f * deltaTime;
            velocity.y = nextVerticalSpeed;

            MoveResult result = VoxelCollision.Move(
                world,
                transform.position.ToNumerics(),
                HalfWidth,
                Height,
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
