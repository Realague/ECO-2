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
        [SerializeField] private StorageSync storageSync;
        [SerializeField] private ClaimSync claimSync;
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
            if (world == null || eye == null || !networkManager.IsListening || appearanceMenu.IsOpen || mapScreen.IsOpen || inventoryScreen.IsOpen || StorageScreen.IsOpen) return null;
            if (networkManager.SpawnManager?.GetLocalPlayerObject() == null) return null;

            // The same ray and reach as PlayerController's strike, so the label names what a click hits.
            System.Numerics.Vector3 origin = eye.transform.position.ToNumerics();
            System.Numerics.Vector3 direction = eye.transform.forward.ToNumerics();
            bool blockHit = VoxelRaycast.Cast(world, origin, direction, PlayerRules.Reach, true, out VoxelHit hit);
            float reached = blockHit ? hit.Distance : PlayerRules.Reach;
            bool storageHit = storageSync.TryAimed(eye.transform.position, eye.transform.forward, reached, out int storageId, out float storageDistance);
            if (storageHit) reached = storageDistance;
            bool carcassHit = ecosystemSync.TryAimedCarcass(eye.transform.position, eye.transform.forward, reached, out int carcassX, out int carcassZ, out int carcassSpecies, out float meat, out float carcassDistance);
            if (carcassHit) reached = carcassDistance;

            // A plant or an animal stands in the column the ray reaches it in, near enough.
            System.Numerics.Vector3 near = origin + direction * Mathf.Max(0f, reached - 0.3f);
            if (ecosystemSync.TryAim(origin, direction, reached, out string name, out int tool)) return OnLand(WithTool(name, tool), Mathf.FloorToInt(near.X), Mathf.FloorToInt(near.Z));
            if (carcassHit) return OnLand(CarcassName(carcassSpecies, meat), carcassX, carcassZ);
            if (storageHit) return StorageName(storageId);
            if (!blockHit) return null;

            ushort block = world.GetBlock(hit.Block);
            return OnLand(WithTool(Loc.Name(Items.NameOf(Blocks.TypeOf(block))), ToolRules.ToBreak(block)), hit.Block.X, hit.Block.Z);
        }

        // What is on someone else's land says so: "Granite · claimed by Alice".
        private string OnLand(string name, int x, int z)
        {
            if (name == null || claimSync == null || claimSync.LocalAllows(x, z)) return name;

            claimSync.TryDescribe(x, z, out string owner, out _);
            return Loc.T("{0} · claimed by {1}", name, string.IsNullOrEmpty(owner) ? Loc.T("someone") : owner);
        }

        // A chest or a stockpile: whose it is, and how full a stockpile is ("Stockpile · Alice (12 / 243)").
        private string StorageName(int storageId)
        {
            if (!storageSync.TryDescribe(storageId, out StorageKind kind, out string owner, out bool yours, out int blocks)) return null;

            string whose = yours ? Loc.T("yours") : owner.Length > 0 ? owner : Loc.T("someone's");
            if (kind == StorageKind.Campfire)
            {
                string fire = storageSync.IsBurning(storageId) ? Loc.T("Campfire · {0} · cooking", whose) : Loc.T("Campfire · {0}", whose);
                return storageSync.IsPublic(storageId) ? Loc.T("{0} · public", fire) : fire;
            }

            string name = kind == StorageKind.Chest
                ? Loc.T("Chest · {0}", whose)
                : kind == StorageKind.SmallStockpile
                    ? Loc.T("Small stockpile · {0} ({1} / {2})", whose, blocks, StorageRules.CapacityOf(kind))
                    : Loc.T("Stockpile · {0} ({1} / {2})", whose, blocks, StorageRules.CapacityOf(kind));
            return storageSync.IsPublic(storageId) ? Loc.T("{0} · public", name) : name;
        }

        // A carcass, and whether enough of it is left to take: "Deer carcass · too far gone".
        private string CarcassName(int speciesIndex, float meat)
        {
            string english = EcosystemSync.DisplayName(ecosystemSync.AnimalName(speciesIndex));
            string name = Loc.T("{0} carcass", Loc.Name(english), Loc.Lower(english));
            return meat < EcosystemSync.CarcassMinMeat ? Loc.T("{0} · too far gone", name) : name;
        }

        // The tool a click would need, when it is not the one in hand: "Granite · needs a pickaxe"
        // ({1}: the tool with its English article, {2}: its translated name in lower case).
        private string WithTool(string name, int tool)
        {
            if (name == null || tool == Items.None || hotbar.SelectedItem == tool) return name;

            string toolName = Items.NameOf(tool);
            string english = toolName.ToLowerInvariant();
            return Loc.T("{0} · needs {1}", name, ("aeiou".IndexOf(english[0]) >= 0 ? "an " : "a ") + english, Loc.Lower(toolName));
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
