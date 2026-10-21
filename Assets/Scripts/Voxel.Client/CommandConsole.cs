using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The command line, opened with T: the player types a command (Command), Enter sends it
    // (CommandSync), Escape closes it. Above the line, the last answers stay a few seconds after
    // it closes. While it is open the player stands still and the game's keys (moving, the map,
    // the inventory, the hotbar) do nothing, so typing never moves or acts. The screen is Assets/UI/Console.uxml.
    public class CommandConsole : MonoBehaviour
    {
        private const int MaxLines = 8;

        // Seconds an answer stays once the line is closed.
        private const float LineLifetime = 10f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private CommandSync commandSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private HotbarHud hotbar;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private UIDocument document;

        // Screen pixels between the hotbar and the line above it.
        private const float InventoryGap = 16f;

        private readonly List<(Label label, float shownAt)> lines = new List<(Label label, float shownAt)>();
        private VisualElement screen;
        private VisualElement log;
        private VisualElement inputRow;
        private TextField input;
        private int openedFrame = -1;
        private static int closedFrame = -1;

        public static bool IsOpen { get; private set; }

        // Also true on the frame the line closes, so that the key that closed it does nothing else.
        public static bool HoldsInput => IsOpen || Time.frameCount == closedFrame;

        // After every OnEnable, so that the document has built its elements.
        private void Start()
        {
            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            log = root.Q("console-log");
            inputRow = root.Q("console-entry");
            input = root.Q<TextField>("console-input");
            input.maxLength = Voxel.Core.Command.MaxLength;
            input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            inputRow.style.display = DisplayStyle.None;
            commandSync.Replied += AddLine;

            // A dedicated server has no one to type.
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            if (commandSync != null) commandSync.Replied -= AddLine;
            IsOpen = false;
        }

        private void Update()
        {
            bool inGame = worldSync.World != null && networkManager.IsListening && !appearanceMenu.IsOpen && !mapScreen.HoldsInput && !inventoryScreen.HoldsInput
                          && networkManager.SpawnManager?.GetLocalPlayerObject() != null;
            screen.style.display = inGame ? DisplayStyle.Flex : DisplayStyle.None;
            if (!inGame)
            {
                if (IsOpen) Close();
                if (!networkManager.IsListening) ClearLines();
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (!IsOpen && keyboard != null && keyboard.tKey.wasPressedThisFrame) Open();

            // The T that opened the line can arrive in it as text, a frame later.
            if (IsOpen && Time.frameCount <= openedFrame + 2 && (input.value == "t" || input.value == "T")) input.value = "";

            PlaceAboveHotbar();

            FadeLines();
        }

        // The line and its answers stand just above the hotbar. Its height is in screen pixels, the
        // panel's in its own units (scaled from 1920 x 1080).
        private void PlaceAboveHotbar()
        {
            float panelHeight = screen.resolvedStyle.height;
            if (float.IsNaN(panelHeight) || panelHeight <= 0f || Screen.height <= 0) return;

            float pixelsToPanel = panelHeight / Screen.height;
            screen.style.paddingBottom = (hotbar.OccupiedHeight + InventoryGap) * pixelsToPanel;
        }

        private void Open()
        {
            IsOpen = true;
            openedFrame = Time.frameCount;
            input.value = "";
            inputRow.style.display = DisplayStyle.Flex;
            input.Focus();
        }

        private void Close()
        {
            IsOpen = false;
            closedFrame = Time.frameCount;
            inputRow.style.display = DisplayStyle.None;
            input.Blur();
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            if (!IsOpen) return;

            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                string text = input.value.Trim();
                if (text.Length > 0)
                {
                    AddLine("> " + text);
                    commandSync.Run(text);
                }

                Close();
                e.StopPropagation();
            }
            else if (e.keyCode == KeyCode.Escape)
            {
                Close();
                e.StopPropagation();
            }
        }

        private void AddLine(string text)
        {
            var label = new Label(text);
            label.AddToClassList("console-line");
            log.Add(label);
            lines.Add((label, Time.unscaledTime));
            while (lines.Count > MaxLines)
            {
                lines[0].label.RemoveFromHierarchy();
                lines.RemoveAt(0);
            }
        }

        // While the line is open every recent answer shows; once it is closed they go after a few seconds.
        private void FadeLines()
        {
            float now = Time.unscaledTime;
            foreach ((Label label, float shownAt) in lines)
            {
                bool visible = IsOpen || now - shownAt < LineLifetime;
                label.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void ClearLines()
        {
            foreach ((Label label, float _) in lines) label.RemoveFromHierarchy();
            lines.Clear();
        }
    }
}
