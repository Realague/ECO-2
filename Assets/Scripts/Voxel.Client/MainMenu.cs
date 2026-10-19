using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The main menu: host a game (one of the saved worlds, or a new one with its name and seed),
    // join one, options (not yet), quit. It also shows the connection and loading progress, and why
    // the last game failed or ended. GameSession does the starting and stopping, and WorldSlots
    // lists and creates the worlds' folders; this class only shows the screen and passes the choices on.
    //
    // The screen is a UI Toolkit document (Assets/UI/MainMenu.uxml and its style sheets). It works
    // with the mouse and the keyboard: the text fields take typing, Enter starts, Escape goes back.
    public class MainMenu : MonoBehaviour
    {
        private enum View
        {
            Home,
            Host,
            NewWorld,
            Join
        }

        private const string HiddenClass = "hidden";

        [SerializeField] private GameSession session;
        [SerializeField] private UIDocument document;

        private VisualElement screen;
        private VisualElement home;
        private VisualElement hostView;
        private VisualElement newWorldView;
        private VisualElement joinView;
        private ScrollView worldList;
        private VisualElement worldEmpty;
        private Button hostStart;
        private TextField newName;
        private TextField newSeed;
        private VisualElement busyView;
        private VisualElement error;
        private Label errorText;
        private Label busyTitle;
        private Label busyText;
        private VisualElement busyProgress;
        private TextField hostPassword;
        private TextField joinAddress;
        private TextField joinPassword;
        private View view;
        private bool wasInGame;

        // The worlds shown in the host view, and the one chosen (-1: none).
        private List<WorldSlots.Slot> slots = new List<WorldSlots.Slot>();
        private readonly List<Button> slotRows = new List<Button>();
        private int selected = -1;

        // After every OnEnable, so that the document has built its elements.
        private void Start()
        {
            Bind(document.rootVisualElement);

            // A dedicated server has no one to show a menu to.
            if (Application.isBatchMode) enabled = false;
            Refresh();
        }

        private void Update()
        {
            Refresh();
            if (!session.InMenu) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            bool enter = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
            if (keyboard.escapeKey.wasPressedThisFrame && view != View.Home) Show(view == View.NewWorld ? View.Host : View.Home);
            else if (enter && view == View.Host) StartHosting();
            else if (enter && view == View.NewWorld) CreateWorld();
            else if (enter && view == View.Join) StartJoining();
        }

        private void Bind(VisualElement root)
        {
            screen = root.Q("screen");
            home = root.Q("view-home");
            hostView = root.Q("view-host");
            newWorldView = root.Q("view-new");
            joinView = root.Q("view-join");
            worldList = root.Q<ScrollView>("world-list");
            worldEmpty = root.Q("world-empty");
            hostStart = root.Q<Button>("host-start");
            newName = root.Q<TextField>("new-name");
            newSeed = root.Q<TextField>("new-seed");
            busyView = root.Q("view-busy");
            error = root.Q("error");
            errorText = root.Q<Label>("error-text");
            busyTitle = root.Q<Label>("busy-title");
            busyText = root.Q<Label>("busy-text");
            busyProgress = root.Q("busy-progress");
            hostPassword = root.Q<TextField>("host-password");
            joinAddress = root.Q<TextField>("join-address");
            joinPassword = root.Q<TextField>("join-password");

            root.Q<Label>("game-title").text = Application.productName;
            root.Q<Label>("host-hint").text =
                $"Players who join need it. Leave it empty for an open game. Players connect on UDP port {session.Port}.";

            Connect(root.Q<Button>("host-button"), () => Show(View.Host));
            Connect(root.Q<Button>("join-button"), () => Show(View.Join));
            Connect(root.Q<Button>("quit-button"), session.Quit);
            Connect(root.Q<Button>("host-back"), () => Show(View.Home));
            Connect(hostStart, StartHosting);
            Connect(root.Q<Button>("world-new"), () => Show(View.NewWorld));
            Connect(root.Q<Button>("new-back"), () => Show(View.Host));
            Connect(root.Q<Button>("new-create"), CreateWorld);
            Connect(root.Q<Button>("join-back"), () => Show(View.Home));
            Connect(root.Q<Button>("join-start"), StartJoining);
            Connect(root.Q<Button>("busy-cancel"), session.Leave);

            // Options do not exist yet.
            Button options = root.Q<Button>("options-button");
            options.SetEnabled(false);
            options.focusable = false;
        }

        // Buttons are not focusable: Enter and Escape are read from the keyboard, and only the
        // text fields take the focus, for typing.
        private static void Connect(Button button, Action action)
        {
            button.focusable = false;
            button.clicked += action;
        }

        private void Show(View next)
        {
            view = next;
            if (next == View.Join)
            {
                joinAddress.value = session.Address;
                joinPassword.value = session.Password;
                joinAddress.Focus();
            }
            else if (next == View.Host)
            {
                ListWorlds();
                hostPassword.Focus();
            }
            else if (next == View.NewWorld)
            {
                newName.value = "";
                newSeed.value = "";
                newName.Focus();
            }

            Refresh();
        }

        // Fills the host view with the saved worlds, the most recently played first and chosen.
        private void ListWorlds()
        {
            slots = WorldSlots.List(session.DataDirectory, new TerrainGenerator().Version);
            worldList.Clear();
            slotRows.Clear();
            for (int i = 0; i < slots.Count; i++)
            {
                int index = i;
                var row = new Button { focusable = false };
                row.AddToClassList("world-row");
                var title = new Label(slots[i].Name);
                title.AddToClassList("world-row-name");
                var details = new Label(DetailsOf(slots[i]));
                details.AddToClassList("world-row-details");
                row.Add(title);
                row.Add(details);
                row.clicked += () => Select(index);

                // A double click plays it.
                row.RegisterCallback<ClickEvent>(click =>
                {
                    if (click.clickCount == 2) StartHosting();
                });
                worldList.Add(row);
                slotRows.Add(row);
            }

            SetVisible(worldList, slots.Count > 0);
            SetVisible(worldEmpty, slots.Count == 0);
            Select(slots.Count > 0 ? 0 : -1);
        }

        private void Select(int index)
        {
            selected = index;
            for (int i = 0; i < slotRows.Count; i++)
            {
                slotRows[i].EnableInClassList("world-row--selected", i == index);
            }

            hostStart.SetEnabled(index >= 0);
            if (index >= 0) worldList.ScrollTo(slotRows[index]);
        }

        // "Seed 1234 · 1024 × 1024 · 19 Oct 2026, 14:02", or what happens if it is played.
        private static string DetailsOf(WorldSlots.Slot slot)
        {
            if (!slot.HasSave) return "Never saved: playing it makes a new world.";

            string played = slot.LastPlayed.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
            string details = $"Seed {slot.Seed} · {slot.ChunksX * Chunk.Size} × {slot.ChunksZ * Chunk.Size} · {played}";
            return slot.IsOutdated ? details + "\nMade by an older version: playing it makes a new world." : details;
        }

        private void StartHosting()
        {
            if (selected < 0 || selected >= slots.Count) return;

            session.HostWorld(slots[selected].Directory, null, hostPassword.value);
        }

        // Creates the folder of the new world and hosts it, with the password of the host view.
        private void CreateWorld()
        {
            int seed = WorldSlots.SeedOf(newSeed.value, UnityEngine.Random.Range(int.MinValue, int.MaxValue));
            string directory = WorldSlots.CreateDirectory(session.DataDirectory, newName.value);
            session.HostWorld(directory, seed, hostPassword.value);
        }

        private void StartJoining()
        {
            session.Join(joinAddress.value, joinPassword.value);
        }

        // Shows what matches the session: nothing in a game, the progress while connecting or
        // loading, otherwise the chosen view and the last problem, if any.
        private void Refresh()
        {
            bool busy = session.IsConnecting || session.IsLoading;
            bool inGame = !session.InMenu && !busy;
            screen.style.display = inGame || !enabled ? DisplayStyle.None : DisplayStyle.Flex;

            // Back from a game, the menu starts over at home.
            if (inGame) wasInGame = true;
            else if (wasInGame && session.InMenu)
            {
                wasInGame = false;
                view = View.Home;
            }

            SetVisible(home, !busy && view == View.Home);
            SetVisible(hostView, !busy && view == View.Host);
            SetVisible(newWorldView, !busy && view == View.NewWorld);
            SetVisible(joinView, !busy && view == View.Join);
            SetVisible(busyView, busy);
            SetVisible(error, !busy && !string.IsNullOrEmpty(session.Message));
            errorText.text = session.Message;

            if (session.IsConnecting)
            {
                busyTitle.text = "Connecting";
                busyText.text = $"Waiting for {session.Address} to answer.";
                busyProgress.style.width = Length.Percent(0f);
            }
            else if (session.IsLoading)
            {
                int percent = Mathf.RoundToInt(session.LoadingProgress * 100f);
                busyTitle.text = "Loading the world";
                busyText.text = $"{percent}%";
                busyProgress.style.width = Length.Percent(percent);
            }
        }

        private static void SetVisible(VisualElement element, bool visible)
        {
            element.EnableInClassList(HiddenClass, !visible);
        }
    }
}
