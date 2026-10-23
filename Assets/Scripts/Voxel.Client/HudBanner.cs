using Eco.Sim;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;
using Voxel.Core;

namespace Voxel.Client
{
    // The banner at the top left (Assets/UI/Hud.uxml): the day and the hour of the simulation's clock
    // (EcosystemSync.StepTime, the same on every machine), the sun or the moon, the speed when it is
    // not 1, and the kind of land under the player (Biomes); key hints at the bottom right. Hidden in
    // the menu, while the character is being created and while the full-screen map is open; the
    // hints also while the inventory is.
    public class HudBanner : MonoBehaviour
    {
        private const float PlaceInterval = 0.25f;
        private const int DawnMinute = 6 * 60;
        private const int DuskMinute = 20 * 60;

        [SerializeField] private UIDocument document;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private BowSync bowSync;

        private VisualElement screen;
        private VisualElement hints;
        private HudGlyph sky;
        private Label day;
        private Label time;
        private Label speed;
        private Label place;
        private Label mapKey;
        private VisualElement draw;
        private VisualElement drawFill;
        private World landscapeWorld;
        private Landscape landscape;
        private float nextPlace;

        private void Start()
        {
            if (Application.isBatchMode)
            {
                enabled = false;
                return;
            }

            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            hints = root.Q("hints");
            sky = root.Q<HudGlyph>("sky");
            day = root.Q<Label>("day");
            time = root.Q<Label>("time");
            speed = root.Q<Label>("speed");
            place = root.Q<Label>("place");
            mapKey = root.Q<Label>("map-key");
            draw = root.Q("draw");
            drawFill = root.Q("draw-fill");
            screen.style.display = DisplayStyle.None;
        }

        private void Update()
        {
            World world = worldSync.World;
            NetworkObject player = world != null && networkManager.IsListening && !appearanceMenu.IsOpen && !mapScreen.IsOpen
                ? networkManager.SpawnManager?.GetLocalPlayerObject()
                : null;
            screen.style.display = player != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (player == null) return;

            hints.style.display = inventoryScreen.IsOpen || StorageScreen.IsOpen ? DisplayStyle.None : DisplayStyle.Flex;
            ShowClock();
            ShowDraw();

            if (Time.unscaledTime >= nextPlace)
            {
                nextPlace = Time.unscaledTime + PlaceInterval;
                ShowPlace(world, player.transform.position);
            }
        }

        // The bow's draw, while the left button is held with it (BowSync.DrawPower).
        private void ShowDraw()
        {
            float power = bowSync.DrawPower;
            draw.EnableInClassList("hidden", power < 0f);
            if (power < 0f) return;

            drawFill.style.width = Length.Percent(power * 100f);
            drawFill.EnableInClassList("hud-draw-fill--full", power >= 1f);
        }

        private void ShowClock()
        {
            long minutes = (long)SimClock.MinutesAt(ecosystemSync.StepTime);
            long dayNumber = minutes / SimClock.MinutesPerDay + 1;
            int minuteOfDay = (int)(minutes % SimClock.MinutesPerDay);
            day.text = $"Day {dayNumber}";
            time.text = $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";

            bool night = minuteOfDay < DawnMinute || minuteOfDay >= DuskMinute;
            sky.Kind = night ? HudGlyph.Shape.Moon : HudGlyph.Shape.Sun;
            sky.EnableInClassList("hud-sky--night", night);

            float multiplier = ecosystemSync.Multiplier;
            speed.text = (multiplier <= 0f ? "paused" : Mathf.Approximately(multiplier, 1f) ? "" : $"×{multiplier:0.##}")
                         + (ecosystemSync.IsBehind ? " (behind)" : "");

            // The map's key as this keyboard's layout shows it (M on QWERTY and AZERTY alike, but elsewhere).
            Keyboard keyboard = Keyboard.current;
            KeyControl key = keyboard?.FindKeyOnCurrentKeyboardLayout("m");
            if (key != null) mapKey.text = key.displayName.ToUpperInvariant();
        }

        // The landscape the world was generated from: the one TerrainGenerator worked out (Landscape
        // keeps the last one), asked for once per world.
        private void ShowPlace(World world, Vector3 position)
        {
            if (world != landscapeWorld)
            {
                landscapeWorld = world;
                landscape = Landscape.For(world.Seed, world.SizeX, world.SizeZ);
            }

            place.text = Biomes.NameAt(world, landscape, Mathf.FloorToInt(position.x), Mathf.FloorToInt(position.z));
        }
    }
}
