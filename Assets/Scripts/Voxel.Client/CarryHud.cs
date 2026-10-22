using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // What the player carries, beside the hotbar (Assets/UI/Hotbar.uxml, the "carry" box): blocks out
    // of 40 and weight out of 30 kg (CarryLimits), each a label over a bar, gold when nearly full and
    // red when full. Shown with the hotbar, whose document it shares; the inventory screen draws the
    // same gauges with Show.
    public class CarryHud : MonoBehaviour
    {
        private const string HighClass = "carry-fill--high";
        private const string FullClass = "carry-fill--full";

        // A gauge turns gold from this share.
        internal const float HighShare = 0.8f;

        [SerializeField] private UIDocument document;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;

        private Label blocksText;
        private Label weightText;
        private VisualElement blocksFill;
        private VisualElement weightFill;
        private int shownBlocks = -1;
        private float shownWeight = -1f;

        private void Start()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            blocksText = root.Q<Label>("carry-blocks");
            weightText = root.Q<Label>("carry-weight");
            blocksFill = root.Q("carry-blocks-fill");
            weightFill = root.Q("carry-weight-fill");
        }

        private void Update()
        {
            Inventory inventory = inventorySync.Inventory;
            int blocks = CarryLimits.BlockCount(inventory);
            float weight = CarryLimits.Weight(inventory, ecosystemSync.ItemWeights);
            if (blocks == shownBlocks && Mathf.Approximately(weight, shownWeight)) return;

            shownBlocks = blocks;
            shownWeight = weight;
            Show(blocks, weight, blocksText, blocksFill, weightText, weightFill);
        }

        internal static void Show(int blocks, float weight, Label blocksText, VisualElement blocksFill, Label weightText, VisualElement weightFill)
        {
            blocksText.text = $"Blocks {blocks} / {CarryLimits.MaxBlocks}";
            weightText.text = $"Weight {weight.ToString("0.0", CultureInfo.InvariantCulture)} / {CarryLimits.MaxWeight:0} kg";
            ShowGauge(blocksFill, (float)blocks / CarryLimits.MaxBlocks);
            ShowGauge(weightFill, weight / CarryLimits.MaxWeight);
        }

        private static void ShowGauge(VisualElement fill, float share)
        {
            bool full = share >= 0.9999f;
            fill.style.width = Length.Percent(Mathf.Clamp01(share) * 100f);
            fill.EnableInClassList(HighClass, share >= HighShare && !full);
            fill.EnableInClassList(FullClass, full);
        }
    }
}
