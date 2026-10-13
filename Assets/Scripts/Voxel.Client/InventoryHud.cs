using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Voxel.Core;

namespace Voxel.Client
{
    // Affiche l'inventaire du joueur local et lit les touches qui s'y rapportent : choisir l'objet tenu,
    // fabriquer, choisir une spécialité, donner. Le texte est dessiné avec OnGUI, pour l'affichage
    // seulement ; les touches sont lues sur le clavier, comme partout ailleurs.
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

        private readonly List<KeyValuePair<int, int>> entries = new List<KeyValuePair<int, int>>();
        private readonly StringBuilder text = new StringBuilder();
        private int heldItem = Items.None;
        private Panel panel;
        private GUIStyle textStyle;

        // L'objet que le joueur tient en main, ou Items.None.
        public int SelectedItem => heldItem;

        private bool InGame => networkManager.IsListening && worldSync.World != null;

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

            // Texte doublé d'une ombre pour rester lisible sur le terrain.
            textStyle.normal.textColor = Color.black;
            GUI.Label(new Rect(area.x + 1f, area.y + 1f, area.width, area.height), content, textStyle);
            textStyle.normal.textColor = Color.white;
            GUI.Label(area, content, textStyle);
        }

        // Recopie l'inventaire dans une liste numérotée. L'objet tenu reste le même tant que le joueur en
        // possède ; sinon il tient le premier objet de la liste.
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

            text.Append("Spécialité : ").Append(Specialities.NameOf(speciality));
            if (speciality == Speciality.None) text.Append(" (K pour en choisir une)");
            text.Append('\n');

            if (entries.Count == 0) text.Append("Inventaire vide : cassez un bloc ou abattez un arbre.\n");
            for (int i = 0; i < entries.Count; i++)
            {
                text.Append(entries[i].Key == heldItem ? "> " : "   ");
                text.Append(i + 1).Append(". ").Append(NameOf(entries[i].Key)).Append(" ×").Append(entries[i].Value).Append('\n');
            }

            text.Append("Molette ou 1-9 : objet tenu   Clic droit : poser ou planter   C : fabriquer   K : spécialité   G : donner (Maj : tout)");
            return text.ToString();
        }

        private void AppendRecipes(Speciality speciality)
        {
            text.Append("Fabrication (chiffre pour fabriquer, C pour fermer)\n");
            for (int i = 0; i < Recipes.All.Count; i++)
            {
                Recipe recipe = Recipes.All[i];
                text.Append("  ").Append(i + 1).Append(". ").Append(NameOf(recipe.Output)).Append(" ×").Append(recipe.OutputCount).Append("  avec  ");
                for (int input = 0; input < recipe.Inputs.Count; input++)
                {
                    if (input > 0) text.Append(" + ");
                    text.Append(NameOf(recipe.Inputs[input].Item)).Append(" ×").Append(recipe.Inputs[input].Count);
                }

                if (!Recipes.HasSkill(recipe, speciality)) text.Append("   (réservé : ").Append(Specialities.NameOf(recipe.Requires)).Append(')');
                else if (!Recipes.HasInputs(recipe, inventorySync.Inventory)) text.Append("   (il manque des ingrédients)");
                text.Append('\n');
            }

            text.Append('\n');
        }

        private void AppendSpecialities(Speciality speciality)
        {
            if (speciality != Speciality.None)
            {
                text.Append("Votre spécialité est définitive : ").Append(Specialities.NameOf(speciality)).Append(", qui ")
                    .Append(Specialities.DescriptionOf(speciality)).Append(".\n\n");
                return;
            }

            text.Append("Choisissez votre spécialité (chiffre, choix définitif ; K pour fermer)\n");
            for (int i = 0; i < Specialities.Choices.Count; i++)
            {
                Speciality choice = Specialities.Choices[i];
                text.Append("  ").Append(i + 1).Append(". ").Append(Specialities.NameOf(choice)).Append(" : ")
                    .Append(Specialities.DescriptionOf(choice)).Append('\n');
            }

            text.Append('\n');
        }

        private string NameOf(int item)
        {
            return Items.IsSeed(item)
                ? $"Graine ({ecosystemSync.PlantName(Items.SeedSpecies(item))})"
                : Items.NameOf(item);
        }

        // Touche de chiffre enfoncée à cette image, de 1 à 9, ou 0. Les touches désignent une position
        // physique : sur un clavier AZERTY, ce sont celles de la rangée du haut, sans Maj.
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
