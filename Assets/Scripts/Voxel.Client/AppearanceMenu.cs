using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;
using Cursor = UnityEngine.Cursor;

namespace Voxel.Client
{
    // Character creation, shown at a player's first connection: the name others see (PlayerNames,
    // unique on the server), then sex, skin tone, eye colour, hair style, hair colour and facial hair,
    // previewed on the player's own model, which the camera faces while the menu is open
    // (PlayerController; it comes close to the face for the eyes and facial hair). A player who has an
    // appearance but no name yet (a save from before names) gets the name alone. The choice is final,
    // so confirming asks once more; a name the server refuses brings the menu back with the reason.
    //
    // The screen is a UI Toolkit document (Assets/UI/CharacterCreation.uxml and its style sheets):
    // this class only fills it in and reacts to it. It works with the mouse (arrows, colour swatches,
    // buttons, and dragging outside the panel to turn the character) and with the keyboard (arrows or
    // ZQSD, Enter, Escape), which it reads itself; the buttons are not focusable, so that UI
    // navigation never competes with those keys.
    public class AppearanceMenu : MonoBehaviour
    {
        private enum Row
        {
            Sex,
            SkinTone,
            EyeColor,
            Hair,
            HairColor,
            FacialHair
        }

        private const int RowCount = 6;
        private const string SelectedRowClass = "option--selected";
        private const string SelectedSwatchClass = "swatch--selected";
        private const string SwatchClass = "swatch";
        private const string SwatchColourClass = "swatch-colour";
        private const string HiddenClass = "hidden";

        // Degrees the character turns per pixel dragged.
        private const float TurnSpeed = 0.4f;

        // Element names in the document, in Row order.
        private static readonly string[] RowNames = { "row-sex", "row-skin-tone", "row-eye-color", "row-hair", "row-hair-color", "row-facial-hair" };

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private AppearanceSync appearanceSync;
        [SerializeField] private UIDocument document;

        private readonly VisualElement[] rows = new VisualElement[RowCount];
        private readonly Label[] values = new Label[RowCount];
        private readonly List<VisualElement>[] swatches = new List<VisualElement>[RowCount];
        private VisualElement screen;
        private VisualElement confirmDialog;
        private Label title;
        private Label subtitle;
        private Label dialogText;
        private TextField nameField;
        private Label nameError;
        private Button confirmButton;

        private PlayerModel model;
        private PlayerModel confirmedModel;

        // The model whose appearance was sent: if its name is refused, only the name is asked again.
        private PlayerModel appearanceSentFor;
        private bool nameOnly;
        private string refusal;
        private Appearance choice;
        private Row row;
        private bool confirming;
        private bool turning;
        private float yaw;

        public bool IsOpen { get; private set; }

        // True while the eye colour or the facial hair is being chosen: the camera then frames the face.
        public bool ShowsFace => IsOpen && !confirming && !nameOnly && (row == Row.EyeColor || row == Row.FacialHair);

        // After every OnEnable, so that the document has built its elements.
        private void Start()
        {
            Bind(document.rootVisualElement);
            screen.style.display = DisplayStyle.None;
            appearanceSync.NameRefused += OnNameRefused;
        }

        private void OnDestroy()
        {
            if (appearanceSync != null) appearanceSync.NameRefused -= OnNameRefused;
        }

        private void Update()
        {
            PlayerModel local = LocalModel();
            bool needed = local != null && worldSync.World != null && (!local.HasChosenAppearance || !local.HasChosenName) && local != confirmedModel;

            if (needed && !IsOpen) Open(local);
            else if (!needed && IsOpen) Close();
            if (!IsOpen) return;

            Turn(Mouse.current);

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            bool enter =keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            if (confirming)
            {
                if (enter) Confirm();
                else if (keyboard.escapeKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame) SetConfirming(false);
                return;
            }

            // While the name is typed, the keys are its letters: only Enter goes on.
            if (nameField.focusController?.focusedElement == nameField)
            {
                if (enter) AskToConfirm();
                return;
            }

            if (nameOnly)
            {
                if (enter) AskToConfirm();
                return;
            }

            // Arrows, or the ZQSD keys (physical W/A/S/D positions).
            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame) Select((Row)(((int)row + RowCount - 1) % RowCount));
            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame) Select((Row)(((int)row + 1) % RowCount));
            if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame) Step(row, -1);
            if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame) Step(row, 1);
            if (enter) AskToConfirm();
        }

        // Asks once more, if the name is valid; otherwise says what a name may be.
        private void AskToConfirm()
        {
            if (!PlayerNames.TryNormalize(nameField.value, out _))
            {
                ShowNameError(AppearanceSync.NameRules);
                nameField.Focus();
                return;
            }

            SetConfirming(true);
        }

        private void OnNameChanged(ChangeEvent<string> changed)
        {
            refusal = null;
            bool valid = PlayerNames.TryNormalize(changed.newValue, out _);
            confirmButton.SetEnabled(valid);
            ShowNameError(valid || changed.newValue.Trim().Length < PlayerNames.MinLength ? null : AppearanceSync.NameRules);
        }

        private void ShowNameError(string message)
        {
            nameError.text = Loc.T(message ?? "");
            nameError.EnableInClassList(HiddenClass, string.IsNullOrEmpty(message));
        }

        // The server refused the name: the menu comes back (the name alone if the appearance went through).
        private void OnNameRefused(string reason)
        {
            refusal = reason;
            confirmedModel = null;
        }

        // While the button pressed outside the panel is held, the mouse turns the character.
        private void Turn(Mouse mouse)
        {
            if (!turning) return;
            if (mouse == null || !mouse.leftButton.isPressed)
            {
                turning = false;
                return;
            }

            yaw -= mouse.delta.ReadValue().x * TurnSpeed;
            model.SetPreviewYaw(yaw);
        }

        // Finds the elements of the document and connects them. Rows with swatches get one per colour.
        private void Bind(VisualElement root)
        {
            screen = root.Q("screen");
            confirmDialog = root.Q("confirm-dialog");
            title = root.Q<Label>("title");
            subtitle = root.Q<Label>("subtitle");
            dialogText = root.Q<Label>("dialog-text");
            nameError = root.Q<Label>("name-error");
            nameField = root.Q<TextField>("name-field");
            nameField.maxLength = PlayerNames.MaxLength;
            nameField.RegisterValueChangedCallback(OnNameChanged);
            confirmButton = root.Q<Button>("confirm");

            // A press on the screen itself, not on the panel or the dialog, starts turning the character.
            screen.RegisterCallback<PointerDownEvent>(pressed =>
            {
                if (pressed.target == screen && pressed.button == 0) turning = true;
            });

            for (int i = 0; i < RowCount; i++)
            {
                var line = (Row)i;
                rows[i] = root.Q(RowNames[i]);
                values[i] = rows[i].Q<Label>("value");

                // Any click in a row selects it, whatever it lands on.
                rows[i].RegisterCallback<PointerDownEvent>(_ => Select(line), TrickleDown.TrickleDown);
                Connect(rows[i].Q<Button>("previous"), () => Step(line, -1));
                Connect(rows[i].Q<Button>("next"), () => Step(line, 1));

                VisualElement palette = rows[i].Q("swatches");
                if (palette != null) swatches[i] = CreateSwatches(palette, line);
            }

            Connect(confirmButton, AskToConfirm);
            Connect(root.Q<Button>("dialog-back"), () => SetConfirming(false));
            Connect(root.Q<Button>("dialog-confirm"), Confirm);
        }

        private static void Connect(Button button, Action action)
        {
            if (button == null) return;

            button.focusable = false;
            button.clicked += action;
        }

        private List<VisualElement> CreateSwatches(VisualElement palette, Row line)
        {
            var created = new List<VisualElement>();
            for (int index = 0; index < ColourCount(line); index++)
            {
                // A frame, styled by the sheet, around the colour itself.
                int colour = index;
                var swatch = new VisualElement();
                swatch.AddToClassList(SwatchClass);
                var fill = new VisualElement { pickingMode = PickingMode.Ignore };
                fill.AddToClassList(SwatchColourClass);
                fill.style.backgroundColor = Colour(line, colour);
                swatch.Add(fill);
                swatch.RegisterCallback<ClickEvent>(_ => Pick(line, colour));
                palette.Add(swatch);
                created.Add(swatch);
            }

            return created;
        }

        private void Open(PlayerModel local)
        {
            model = local;
            choice = local.Appearance;
            row = Row.Sex;
            IsOpen = true;
            turning = false;
            yaw = 0f;
            nameOnly = local.HasChosenAppearance || local == appearanceSentFor;
            for (int i = 0; i < RowCount; i++)
            {
                rows[i].EnableInClassList(HiddenClass, nameOnly);
            }

            title.text = Loc.T(nameOnly ? "Choose your name" : "Create your character");
            subtitle.text = Loc.T(nameOnly
                ? "Players now have names: this is how others will know you. The choice is final."
                : "This is how other players will see you. The choice is final.");
            dialogText.text = Loc.T(nameOnly ? "Keep this name? It cannot be changed later." : "Keep this name and appearance? They cannot be changed later.");
            confirmButton.SetEnabled(PlayerNames.TryNormalize(nameField.value, out _));
            ShowNameError(refusal);
            SetConfirming(false);
            screen.style.display = DisplayStyle.Flex;
            Cursor.lockState = CursorLockMode.None;
            model.SetHeadVisible(true);
            Refresh();
            nameField.schedule.Execute(() => nameField.Focus());
        }

        private void Close()
        {
            IsOpen = false;
            confirming = false;
            turning = false;
            screen.style.display = DisplayStyle.None;
            if (model != null)
            {
                model.SetHeadVisible(false);
                model.SetPreviewYaw(0f);
            }
            model = null;
        }

        private void Select(Row line)
        {
            row = line;
            Refresh();
        }

        // The previous or next option of a row with arrows, or the neighbouring colour of a palette.
        private void Step(Row line, int step)
        {
            row = line;
            Show(Change(choice, line, step));
        }

        private void Pick(Row line, int colour)
        {
            row = line;
            Show(WithColour(choice, line, colour));
        }

        private void Show(Appearance appearance)
        {
            choice = appearance;
            model.Preview(choice);
            Refresh();
        }

        private void SetConfirming(bool value)
        {
            confirming = value;
            confirmDialog.EnableInClassList(HiddenClass, !value);
        }

        private void Confirm()
        {
            if (!IsOpen || !PlayerNames.TryNormalize(nameField.value, out string name)) return;

            if (!nameOnly)
            {
                appearanceSync.RequestAppearance(choice);
                appearanceSentFor = model;
            }

            refusal = null;
            appearanceSync.RequestName(name);
            confirmedModel = model;
            Close();
        }

        // Puts the current choice and the selected row on screen.
        private void Refresh()
        {
            for (int i = 0; i < RowCount; i++)
            {
                var line = (Row)i;
                rows[i].EnableInClassList(SelectedRowClass, line == row);
                values[i].text = ValueText(choice, line);

                if (swatches[i] == null) continue;

                int selected = ColourIndex(choice, line);
                for (int colour = 0; colour < swatches[i].Count; colour++)
                {
                    swatches[i][colour].EnableInClassList(SelectedSwatchClass, colour == selected);
                }
            }
        }

        private PlayerModel LocalModel()
        {
            if (!networkManager.IsListening || !networkManager.IsClient) return null;

            NetworkObject player = networkManager.LocalClient?.PlayerObject;
            return player != null ? player.GetComponent<PlayerModel>() : null;
        }

        private static string ValueText(Appearance appearance, Row line)
        {
            switch (line)
            {
                case Row.Sex: return Loc.Name(Appearance.NameOf(appearance.Sex));
                case Row.SkinTone: return $"{appearance.SkinTone + 1} / {Appearance.SkinToneCount}";
                case Row.EyeColor: return Loc.In("eyes", Appearance.EyeColorNames[appearance.EyeColor]);
                case Row.Hair: return Loc.Name(Appearance.NameOf(appearance.Hair));
                case Row.HairColor: return Loc.In("hair", Appearance.HairColorNames[appearance.HairColor]);
                default: return Loc.Name(Appearance.NameOf(appearance.FacialHair));
            }
        }

        private static Appearance Change(Appearance appearance, Row line, int step)
        {
            switch (line)
            {
                case Row.Sex: return appearance.WithSex((int)appearance.Sex + step);
                case Row.SkinTone: return appearance.WithSkinTone(appearance.SkinTone + step);
                case Row.EyeColor: return appearance.WithEyeColor(appearance.EyeColor + step);
                case Row.Hair: return appearance.WithHair((int)appearance.Hair + step);
                case Row.HairColor: return appearance.WithHairColor(appearance.HairColor + step);
                default: return appearance.WithFacialHair((int)appearance.FacialHair + step);
            }
        }

        // Rows that choose a colour: how many colours, which one is chosen, which one it is.
        private static int ColourCount(Row line)
        {
            switch (line)
            {
                case Row.SkinTone: return Appearance.SkinToneCount;
                case Row.EyeColor: return Appearance.EyeColorCount;
                case Row.HairColor: return Appearance.HairColorCount;
                default: return 0;
            }
        }

        private static int ColourIndex(Appearance appearance, Row line)
        {
            switch (line)
            {
                case Row.SkinTone: return appearance.SkinTone;
                case Row.EyeColor: return appearance.EyeColor;
                default: return appearance.HairColor;
            }
        }

        private static Color Colour(Row line, int index)
        {
            switch (line)
            {
                case Row.SkinTone: return PlayerSkin.SkinTone(index);
                case Row.EyeColor: return PlayerSkin.EyeColour(index);
                default: return PlayerSkin.HairColour(index);
            }
        }

        private static Appearance WithColour(Appearance appearance, Row line, int index)
        {
            switch (line)
            {
                case Row.SkinTone: return appearance.WithSkinTone(index);
                case Row.EyeColor: return appearance.WithEyeColor(index);
                default: return appearance.WithHairColor(index);
            }
        }
    }
}
