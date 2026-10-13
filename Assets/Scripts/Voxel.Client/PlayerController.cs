using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Joueur à la première personne. transform.position est le centre des pieds.
    // Seul le propriétaire simule son joueur ; les autres copies suivent le NetworkTransform.
    public class PlayerController : NetworkBehaviour
    {
        private const float MaxFallSpeed = 50f;
        private const float MaxDeltaTime = 0.05f;
        private const float MaxPitch = 89f;
        private const float CrosshairSize = 6f;

        [SerializeField] private GameObject body;
        [SerializeField] private float moveSpeed = 5f;
        [SerializeField] private float jumpHeight = 1.25f;
        [SerializeField] private float gravity = 25f;
        [SerializeField] private float mouseSensitivity = 0.1f;

        private WorldSync worldSync;
        private EcosystemSync ecosystemSync;
        private World world;
        private Transform cameraTransform;
        private Vector3 velocity;
        private bool grounded;
        private float pitch;
        private ushort selectedBlock = Blocks.Dirt;

        private void Awake()
        {
            // Activé seulement pour le propriétaire, une fois apparu sur le réseau.
            enabled = false;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) return;

            worldSync = FindAnyObjectByType<WorldSync>();
            ecosystemSync = FindAnyObjectByType<EcosystemSync>();
            cameraTransform = Camera.main.transform;
            body.SetActive(false);
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
            // Sur un client, le joueur apparaît avant que le serveur ait envoyé le monde : on attend.
            world = worldSync.World;
            if (world == null) return;

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            bool hasDevices = keyboard != null && mouse != null;

            if (hasDevices && keyboard.escapeKey.wasPressedThisFrame)
            {
                Cursor.lockState = CursorLockMode.None;
            }

            bool hasControl = hasDevices && Cursor.lockState == CursorLockMode.Locked;
            if (hasControl)
            {
                Look(mouse);
            }

            // Sans clavier ni souris, la gravité s'applique quand même.
            Move(keyboard, hasControl);
            PlaceCamera();

            if (hasControl)
            {
                SelectBlock(keyboard);
                Interact(mouse);
            }
            else if (hasDevices && mouse.leftButton.wasPressedThisFrame)
            {
                // Le premier clic capture la souris, sans casser de bloc.
                Cursor.lockState = CursorLockMode.Locked;
            }
        }

        private void OnGUI()
        {
            if (world == null) return;

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
        }

        // La caméra de la scène suit le joueur local sans lui être rattachée : elle survit à sa disparition.
        private void PlaceCamera()
        {
            cameraTransform.SetPositionAndRotation(
                transform.position + Vector3.up * PlayerRules.EyeHeight,
                Quaternion.Euler(pitch, transform.eulerAngles.y, 0f));
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
            bool blockHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, out VoxelHit hit);

            // Le clic gauche abat la plante visée si elle se trouve avant le bloc visé.
            if (breakBlock)
            {
                float blockDistance = blockHit ? hit.Distance : PlayerRules.Reach;
                if (ecosystemSync.TryPick(origin, direction, blockDistance, out int plantId))
                {
                    ecosystemSync.RequestFell(plantId);
                    return;
                }
            }

            if (!blockHit) return;

            // Le serveur a le dernier mot : la demande passe par WorldSync, jamais par World.SetBlock.
            if (breakBlock)
            {
                worldSync.RequestSetBlock(hit.Block, Blocks.Air);
                return;
            }

            BlockPos target = hit.Block + hit.Normal;
            bool insidePlayer = VoxelCollision.IntersectsBlock(
                transform.position.ToNumerics(), PlayerRules.HalfWidth, PlayerRules.Height, target);
            if (target != hit.Block && !insidePlayer)
            {
                worldSync.RequestSetBlock(target, selectedBlock);
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
