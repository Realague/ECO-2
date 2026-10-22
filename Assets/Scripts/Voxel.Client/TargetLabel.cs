using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // A small label under the crosshair naming what the local player aims at within reach: the animal
    // or plant a strike would hit (EcosystemSync.TryAim), otherwise the block, with the tool it needs
    // when that is not the one in hand. Display only. It is
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
        [SerializeField] private HotbarHud hotbar;
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
            if (ecosystemSync.TryAim(origin, direction, blockHit ? hit.Distance : PlayerRules.Reach, out string name, out int tool)) return WithTool(name, tool);
            if (!blockHit) return null;

            ushort block = world.GetBlock(hit.Block);
            return WithTool(Items.NameOf(Blocks.TypeOf(block)), ToolRules.ToBreak(block));
        }

        // The tool a click would need, when it is not the one in hand: "Granite · needs a pickaxe".
        private string WithTool(string name, int tool)
        {
            if (name == null || tool == Items.None || hotbar.SelectedItem == tool) return name;

            string toolName = Items.NameOf(tool).ToLowerInvariant();
            return $"{name} · needs {("aeiou".IndexOf(toolName[0]) >= 0 ? "an" : "a")} {toolName}";
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
