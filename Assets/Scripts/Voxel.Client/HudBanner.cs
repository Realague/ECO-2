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

        // A damage number rises this many blocks over its life, pops in over PopSeconds from PopScale
        // and fades out over the last FadeSeconds.
        private const float DamageSeconds = 1.2f;
        private const float DamageRise = 0.8f;
        private const float PopSeconds = 0.15f;
        private const float PopScale = 1.4f;
        private const float FadeSeconds = 0.5f;

        private struct DamageNumber
        {
            public Label Label;
            public Vector3 Point;
            public float Start;
        }

        [SerializeField] private UIDocument document;
        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private BowSync bowSync;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private InventorySync inventorySync;

        private VisualElement screen;
        private VisualElement hints;
        private HudGlyph sky;
        private Label day;
        private Label time;
        private Label speed;

        // "Creative mode", under the banner while the local player plays in it.
        private VisualElement creativeTag;
        private Label place;
        private Label mapKey;
        private VisualElement draw;
        private VisualElement drawFill;
        private VisualElement damageLayer;
        private Nameplates nameplates;
        private readonly System.Collections.Generic.List<DamageNumber> damageNumbers = new System.Collections.Generic.List<DamageNumber>();
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
            damageLayer = root.Q("damage-numbers");
            creativeTag = root.Q("creative-tag");
            nameplates = new Nameplates(root.Q("nameplates"));
            screen.style.display = DisplayStyle.None;
        }

        // The damage an arrow of the local player dealt, shown rising from point (where it hit):
        // gold and larger for a head shot, red and saying so when it killed.
        public void ShowDamage(Vector3 point, float damage, bool head, bool killed)
        {
            if (damageLayer == null) return;

            string text = $"-{Mathf.Max(1, Mathf.RoundToInt(damage))}";
            if (head) text += " " + Loc.T("Head");
            if (killed) text += " · " + Loc.T("Killed");
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList("damage-number");
            label.EnableInClassList("damage-number--head", head);
            label.EnableInClassList("damage-number--kill", killed);
            damageLayer.Add(label);
            damageNumbers.Add(new DamageNumber { Label = label, Point = point, Start = Time.time });
            PlaceDamageNumbers();
        }

        // Each number over its point (hidden behind the camera), rising, popping in, then fading.
        private void PlaceDamageNumbers()
        {
            Camera eye = Camera.main;
            IPanel panel = damageLayer.panel;
            float now = Time.time;
            for (int i = damageNumbers.Count - 1; i >= 0; i--)
            {
                DamageNumber number = damageNumbers[i];
                float age = now - number.Start;
                if (age >= DamageSeconds || eye == null || panel == null)
                {
                    number.Label.RemoveFromHierarchy();
                    damageNumbers.RemoveAt(i);
                    continue;
                }

                Vector3 point = number.Point + Vector3.up * (DamageRise * age / DamageSeconds);
                bool inFront = Vector3.Dot(point - eye.transform.position, eye.transform.forward) > 0.1f;
                number.Label.style.display = inFront ? DisplayStyle.Flex : DisplayStyle.None;
                if (!inFront) continue;

                Vector2 onPanel = RuntimePanelUtils.CameraTransformWorldToPanel(panel, point, eye);
                number.Label.style.left = onPanel.x;
                number.Label.style.top = onPanel.y;
                float pop = Mathf.Lerp(PopScale, 1f, Mathf.Clamp01(age / PopSeconds));
                number.Label.style.scale = new Scale(new Vector3(pop, pop, 1f));
                number.Label.style.opacity = Mathf.Clamp01((DamageSeconds - age) / FadeSeconds);
            }
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
            // Not over the inventory's or a storage's tab bar.
            bool creativeShown = inventorySync != null && inventorySync.Creative && !inventoryScreen.IsOpen && !StorageScreen.IsOpen;
            if (creativeTag != null) creativeTag.style.display = creativeShown ? DisplayStyle.Flex : DisplayStyle.None;
            ShowClock();
            ShowDraw();

            if (Time.unscaledTime >= nextPlace)
            {
                nextPlace = Time.unscaledTime + PlaceInterval;
                ShowPlace(world, player.transform.position);
            }
        }

        // Once the camera has moved for this frame (PlayerController places it in Update).
        private void LateUpdate()
        {
            if (damageNumbers.Count > 0) PlaceDamageNumbers();
            nameplates?.Place(networkManager, worldSync.World, Camera.main);
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
            day.text = Loc.T("Day {0}", dayNumber);
            time.text = $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";

            bool night = minuteOfDay < DawnMinute || minuteOfDay >= DuskMinute;
            sky.Kind = night ? HudGlyph.Shape.Moon : HudGlyph.Shape.Sun;
            sky.EnableInClassList("hud-sky--night", night);

            float multiplier = ecosystemSync.Multiplier;
            speed.text = (multiplier <= 0f ? Loc.T("paused") : Mathf.Approximately(multiplier, 1f) ? "" : $"×{multiplier:0.##}")
                         + (ecosystemSync.IsBehind ? " " + Loc.T("(behind)") : "");

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

            int x = Mathf.FloorToInt(position.x);
            int z = Mathf.FloorToInt(position.z);
            string biome = Loc.Name(Biomes.NameAt(world, landscape, x, z));
            place.text = claimSync != null && claimSync.TryDescribe(x, z, out string owner, out ClaimRelation relation)
                ? Loc.T("{0} · {1}", biome, LandOf(owner, relation))
                : biome;
        }

        // Whose land it is: "Your land", "Alice's land".
        internal static string LandOf(string owner, ClaimRelation relation) =>
            relation == ClaimRelation.Yours ? Loc.T("Your land") : Loc.T("{0}'s land", string.IsNullOrEmpty(owner) ? Loc.T("someone") : owner);
    }
}
