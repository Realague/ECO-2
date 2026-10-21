using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A small label under the crosshair naming what the local player aims at within reach: the animal
    // or plant a strike would hit (EcosystemSync.TryAim), otherwise the block. Display only. It is
    // hidden in the menu, while character creation or the full-screen map is open, and when nothing
    // is in reach. The screen is Assets/UI/Target.uxml.
    public class TargetLabel : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private UIDocument document;

        private VisualElement screen;
        private Label label;
        private string shown;

        // After every OnEnable, so that the document has built its elements.
        private void Start()
        {
            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            label = root.Q<Label>("target-name");
            screen.style.display = DisplayStyle.None;

            // A dedicated server has no one to show it to.
            if (Application.isBatchMode) enabled = false;
        }

        private void Update()
        {
            Show(AimedName());
        }

        private string AimedName()
        {
            World world = worldSync.World;
            Camera eye = Camera.main;
            if (world == null || eye == null || !networkManager.IsListening || appearanceMenu.IsOpen || mapScreen.IsOpen || inventoryScreen.IsOpen) return null;
            if (networkManager.SpawnManager?.GetLocalPlayerObject() == null) return null;

            // The same ray and reach as PlayerController's strike, so the label names what a click hits.
            System.Numerics.Vector3 origin = eye.transform.position.ToNumerics();
            System.Numerics.Vector3 direction = eye.transform.forward.ToNumerics();
            bool blockHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, out VoxelHit hit);
            if (ecosystemSync.TryAim(origin, direction, blockHit ? hit.Distance : PlayerRules.Reach, out string name)) return name;

            return blockHit ? Items.NameOf(Blocks.TypeOf(world.GetBlock(hit.Block))) : null;
        }

        private void Show(string name)
        {
            if (name == shown) return;

            shown = name;
            screen.style.display = name != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (name != null) label.text = name;
        }
    }
}
