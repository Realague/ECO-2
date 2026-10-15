using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The main menu: host a game, join one, options (not yet), quit. It also shows the connection
    // and loading progress, and why the last game failed or ended. GameSession does the starting
    // and stopping; this class only shows the screen and passes the choices on.
    //
    // The screen is a UI Toolkit document (Assets/UI/MainMenu.uxml and its style sheets). It works
    // with the mouse and the keyboard: the text fields take typing, Enter starts, Escape goes back.
    public class MainMenu : MonoBehaviour
    {
        private enum View
        {
            Home,
            Host,
            Join
        }

        private const string HiddenClass = "hidden";

        [SerializeField] private GameSession session;
        [SerializeField] private UIDocument document;

        private VisualElement screen;
        private VisualElement home;
        private VisualElement hostView;
        private VisualElement joinView;
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
            if (keyboard.escapeKey.wasPressedThisFrame && view != View.Home) Show(View.Home);
            else if (enter && view == View.Host) StartHosting();
            else if (enter && view == View.Join) StartJoining();
        }

        private void Bind(VisualElement root)
        {
            screen = root.Q("screen");
            home = root.Q("view-home");
            hostView = root.Q("view-host");
            joinView = root.Q("view-join");
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
            Connect(root.Q<Button>("host-start"), StartHosting);
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
                hostPassword.value = "";
                hostPassword.Focus();
            }

            Refresh();
        }

        private void StartHosting()
        {
            session.Host(hostPassword.value);
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
