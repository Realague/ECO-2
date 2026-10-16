using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Shows the local player's inventory and reads the related keys: choosing the held item,
    // crafting, choosing a speciality, giving. The text is drawn with OnGUI, for display
    // only; keys are read from the keyboard, as everywhere else.
    public class InventoryHud : MonoBehaviour
    {
        private enum Panel
        {
            None,
            Crafting,
            Speciality
        }

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private InventorySync inventorySync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;

        private readonly List<KeyValuePair<int, int>> entries = new List<KeyValuePair<int, int>>();
        private readonly StringBuilder text = new StringBuilder();
        private int heldItem = Items.None;
        private Panel panel;
        private GUIStyle textStyle;

        // The item the player holds in hand, or Items.None.
        public int SelectedItem => heldItem;

        // Hidden while the character is being created and while the full-screen map is open.
        private bool InGame => networkManager.IsListening && worldSync.World != null && !appearanceMenu.IsOpen && !mapScreen.IsOpen;

        private void Update()
        {
            if (!InGame)
            {
                panel = Panel.None;
                heldItem = Items.None;
                return;
            }

            RefreshEntries();

            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (keyboard == null || mouse == null || Cursor.lockState != CursorLockMode.Locked) return;

            if (keyboard.cKey.wasPressedThisFrame) panel = panel == Panel.Crafting ? Panel.None : Panel.Crafting;
            if (keyboard.kKey.wasPressedThisFrame) panel = panel == Panel.Speciality ? Panel.None : Panel.Speciality;

            int digit = PressedDigit(keyboard);
            if (panel == Panel.Crafting)
            {
                if (digit > 0) inventorySync.RequestCraft(digit - 1);
            }
            else if (panel == Panel.Speciality)
            {
                if (digit > 0 && digit <= Specialities.Choices.Count && inventorySync.Speciality == Speciality.None)
                {
                    inventorySync.RequestSpeciality(Specialities.Choices[digit - 1]);
                    panel = Panel.None;
                }
            }
            else
            {
                if (digit > 0 && digit <= entries.Count) heldItem = entries[digit - 1].Key;

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f && entries.Count > 0)
                {
                    int index = Mathf.Max(0, IndexOfHeld()) + (scroll > 0f ? -1 : 1);
                    heldItem = entries[(index + entries.Count) % entries.Count].Key;
                }
            }

            if (keyboard.gKey.wasPressedThisFrame && heldItem != Items.None)
            {
                bool wholeStack = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
                inventorySync.RequestGive(heldItem, wholeStack ? inventorySync.Inventory.Count(heldItem) : 1);
            }
        }

        private void OnGUI()
        {
            if (!InGame) return;

            if (textStyle == null)
            {
                textStyle = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            }

            string content = BuildText();
            float width = Screen.width - 40f;
            float height = textStyle.CalcHeight(new GUIContent(content), width);
            var area = new Rect(20f, Screen.height - height - 20f, width, height);

            // Text doubled with a shadow to stay readable on the terrain.
            textStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), content, textStyle);
            textStyle.normal.textColor = Color.white;
            GUI.Label(area, content, textStyle);
        }

        // Copies the inventory into a numbered list. The held item stays the same as long as the player
        // owns some; otherwise they hold the first item of the list.
        private void RefreshEntries()
        {
            entries.Clear();
            entries.AddRange(inventorySync.Inventory.Entries);

            if (IndexOfHeld() < 0) heldItem = entries.Count > 0 ? entries[0].Key : Items.None;
        }

        private int IndexOfHeld()
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Key == heldItem) return i;
            }

            return -1;
        }

        private string BuildText()
        {
            text.Clear();
            Speciality speciality = inventorySync.Speciality;

            if (panel == Panel.Crafting) AppendRecipes(speciality);
            else if (panel == Panel.Speciality) AppendSpecialities(speciality);

            text.Append("Speciality: ").Append(Specialities.NameOf(speciality));
            if (speciality == Speciality.None) text.Append(" (K to choose one)");
            text.Append('\n');

            if (entries.Count == 0) text.Append("Empty inventory: break a block or fell a tree.\n");
            for (int i = 0; i < entries.Count; i++)
            {
                text.Append(entries[i].Key == heldItem ? "> " : "   ");
                text.Append(i + 1).Append(". ").Append(NameOf(entries[i].Key)).Append(" ×").Append(entries[i].Value).Append('\n');
            }

            text.Append("Wheel or 1-9: held item   Right click: place or plant   C: craft   K: speciality   G: give (Shift: all)   M: map");
            return text.ToString();
        }

        private void AppendRecipes(Speciality speciality)
        {
            text.Append("Crafting (digit to craft, C to close)\n");
            for (int i = 0; i < Recipes.All.Count; i++)
            {
                Recipe recipe = Recipes.All[i];
                text.Append("  ").Append(i + 1).Append(". ").Append(NameOf(recipe.Output)).Append(" ×").Append(recipe.OutputCount).Append("  from  ");
                for (int input = 0; input < recipe.Inputs.Count; input++)
                {
                    if (input > 0) text.Append(" + ");
                    text.Append(NameOf(recipe.Inputs[input].Item)).Append(" ×").Append(recipe.Inputs[input].Count);
                }

                if (!Recipes.HasSkill(recipe, speciality)) text.Append("   (reserved: ").Append(Specialities.NameOf(recipe.Requires)).Append(')');
                else if (!Recipes.HasInputs(recipe, inventorySync.Inventory)) text.Append("   (missing ingredients)");
                text.Append('\n');
            }

            text.Append('\n');
        }

        private void AppendSpecialities(Speciality speciality)
        {
            if (speciality != Speciality.None)
            {
                text.Append("Your speciality is permanent: ").Append(Specialities.NameOf(speciality)).Append(", who ")
                    .Append(Specialities.DescriptionOf(speciality)).Append(".\n\n");
                return;
            }

            text.Append("Choose your speciality (digit, permanent choice; K to close)\n");
            for (int i = 0; i < Specialities.Choices.Count; i++)
            {
                Speciality choice = Specialities.Choices[i];
                text.Append("  ").Append(i + 1).Append(". ").Append(Specialities.NameOf(choice)).Append(": ")
                    .Append(Specialities.DescriptionOf(choice)).Append('\n');
            }

            text.Append('\n');
        }

        private string NameOf(int item)
        {
            return Items.IsSeed(item)
                ? $"Seed ({ecosystemSync.PlantName(Items.SeedSpecies(item))})"
                : Items.NameOf(item);
        }

        // Digit key pressed this frame, from 1 to 9, or 0. Keys designate a physical position:
        // on an AZERTY keyboard, they are those of the top row, without Shift.
        private static int PressedDigit(Keyboard keyboard)
        {
            if (keyboard.digit1Key.wasPressedThisFrame) return 1;
            if (keyboard.digit2Key.wasPressedThisFrame) return 2;
            if (keyboard.digit3Key.wasPressedThisFrame) return 3;
            if (keyboard.digit4Key.wasPressedThisFrame) return 4;
            if (keyboard.digit5Key.wasPressedThisFrame) return 5;
            if (keyboard.digit6Key.wasPressedThisFrame) return 6;
            if (keyboard.digit7Key.wasPressedThisFrame) return 7;
            if (keyboard.digit8Key.wasPressedThisFrame) return 8;
            if (keyboard.digit9Key.wasPressedThisFrame) return 9;
            return 0;
        }
    }
}
