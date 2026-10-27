using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Voxel.Client
{
    // The full-screen map, opened and closed with M (Escape also closes it): the world's picture
    // (WorldMap) with the other players and the player's arrow (no animal dots: the species layers
    // show where animals live). The mouse is freed while it is open: the wheel zooms around the
    // pointer, dragging moves the map, Space centres it on the player
    // again, a right click teleports the player there (the "tp" command, CommandSync), and the
    // coordinates under the pointer are shown below. Tab lays the climate over the
    // terrain: temperature, then humidity, from the world's geography. The list beside it
    // (SpeciesPanel) shows how many of each species live in the world, and lays where the chosen one
    // lives over the map, densest in yellow (from a census, EcosystemSync.RequestCensus, asked for
    // every CensusInterval while the map is open). Meanwhile the player stands still and the other
    // on-screen texts are hidden. The screen is Assets/UI/Map.uxml.
    public class MapScreen : MonoBehaviour
    {
        private const float StartPixelsPerBlock = 3f;
        private const float MaxPixelsPerBlock = 12f;
        private const float ZoomStep = 1.25f;

        // How often the census behind the species list and layer is asked for while the map is open.
        private const float CensusInterval = 2f;

        // A press that moves less than this is a click (claims a plot in plot mode), not a drag.
        private const float ClickThreshold = 8f;

        // How long a refusal stays in the land panel.
        private const float LandMessageSeconds = 4f;

        private enum Layer
        {
            Terrain,
            Temperature,
            Humidity
        }

        private static readonly string[] LayerNames = { "Terrain", "Temperature: blue cold, red hot", "Humidity: ochre dry, blue wet" };

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldMap worldMap;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private CommandSync commandSync;
        [SerializeField] private UIDocument document;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private ClaimSync claimSync;

        private VisualElement screen;
        private VisualElement view;
        private VisualElement image;
        private VisualElement climate;
        private VisualElement speciesLayer;
        private SpeciesPanel speciesPanel;
        private Label layerName;
        private Label pointerText;
        private MapMarkers markers;
        private MapMarkers.Projection projection;
        private Texture2D shownTexture;

        // The world column (x, z) shown in the middle of the view, and the scale. The view follows
        // the player until it is dragged or zoomed elsewhere.
        private Vector2 centre;
        private float pixelsPerBlock = StartPixelsPerBlock;
        private float minPixelsPerBlock;
        private bool following;
        private Vector2 viewSize;

        private bool dragging;
        private Vector2 dragStart;
        private Vector2 dragCentre;
        private int closedFrame = -1;

        // The climate layer shown, and its pictures for the current world (one pixel per geography cell).
        private Layer layer;
        private Voxel.Core.World layerWorld;
        private Texture2D temperatureLayer;
        private Texture2D humidityLayer;
        private Texture2D shownLayerTexture;

        // The species layer: its picture (one pixel per census cell), the census it was drawn from,
        // and when to ask for the next one.
        private Texture2D speciesTexture;
        private int shownCensus = -1;
        private float nextCensus;

        // Plot mode (C): the claimed plots, the one under the pointer, and the land panel in the
        // species list's place.
        private VisualElement claimsLayer;
        private VisualElement claimHover;
        private VisualElement speciesPanelRoot;
        private VisualElement landPanel;
        private Label landCount;
        private Label landPointer;
        private Label landMessage;
        private VisualElement landList;
        private VisualElement landEmpty;
        private Toggle landShowWorld;
        private Texture2D shownClaims;
        private bool plotMode;
        private bool pointerInside;
        private Vector2 pointerLocal;
        private Vector2 pressAt;
        private float landMessageUntil;

        public bool IsOpen { get; private set; }

        // Also true on the frame the map closes, so that the key that closed it does nothing else.
        public bool HoldsInput => IsOpen || Time.frameCount == closedFrame;

        // After every OnEnable, so that the document has built its elements.
        private void Start()
        {
            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            view = root.Q("map-view");
            image = root.Q("map-image");
            climate = root.Q("map-climate");
            speciesLayer = root.Q("map-species");
            layerName = root.Q<Label>("layer-name");
            pointerText = root.Q<Label>("pointer-position");
            markers = new MapMarkers(null, root.Q("map-players"), root.Q("map-arrow"));
            projection = Project;
            speciesPanel = new SpeciesPanel(root, ecosystemSync, worldMap);
            speciesPanel.SelectionChanged += OnSpeciesChosen;
            Loc.Changed += OnLanguageChanged;
            speciesLayer.style.display = DisplayStyle.None;
            claimsLayer = root.Q("map-claims");
            claimHover = root.Q("map-claim-hover");
            speciesPanelRoot = root.Q("species-panel");
            landPanel = root.Q("land-panel");
            landCount = root.Q<Label>("land-count");
            landPointer = root.Q<Label>("land-pointer");
            landMessage = root.Q<Label>("land-message");
            landList = root.Q("land-list");
            landEmpty = root.Q("land-empty");
            landShowWorld = root.Q<Toggle>("land-show-world");
            landShowWorld.focusable = false;
            landShowWorld.RegisterValueChangedCallback(e => ClaimOverlay.SetShown(e.newValue));
            if (claimSync != null)
            {
                claimSync.AccessChanged += ShowLand;
                claimSync.PlotChanged += OnPlotChanged;
                claimSync.Refused += OnClaimRefused;
            }

            root.Q<Button>("tab-map").focusable = false;

            view.RegisterCallback<WheelEvent>(OnWheel);
            view.RegisterCallback<PointerDownEvent>(OnPointerDown);
            view.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            view.RegisterCallback<PointerUpEvent>(OnPointerUp);
            view.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                pointerText.text = "";
                pointerInside = false;
            });
            screen.style.display = DisplayStyle.None;
            ShowLayer(Layer.Terrain);

            // A dedicated server has no one to show a map to.
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            Loc.Changed -= OnLanguageChanged;
            if (claimSync != null)
            {
                claimSync.AccessChanged -= ShowLand;
                claimSync.PlotChanged -= OnPlotChanged;
                claimSync.Refused -= OnClaimRefused;
            }

            DropLayers();
            if (speciesTexture != null) Destroy(speciesTexture);
        }

        private void OnLanguageChanged()
        {
            speciesPanel.Retranslate();
            ShowLayerName();
            ShowLand();
        }

        private void Update()
        {
            NetworkObject player = worldMap.Texture != null && networkManager.IsListening && !appearanceMenu.IsOpen
                ? networkManager.SpawnManager?.GetLocalPlayerObject()
                : null;
            if (player == null)
            {
                if (IsOpen) Close(false);
                return;
            }

            // An M typed in the command line, or in the species search, does not toggle the map;
            // Escape then leaves the search first.
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && IsOpen && speciesPanel.HasFocus)
            {
                if (keyboard.escapeKey.wasPressedThisFrame) speciesPanel.Blur();
            }
            else if (keyboard != null && !CommandConsole.HoldsInput && !StorageScreen.HoldsInput)
            {
                if (MapKey(keyboard).wasPressedThisFrame)
                {
                    if (IsOpen)
                    {
                        Close(true);
                    }
                    else
                    {
                        // From the inventory, M goes straight to the map.
                        inventoryScreen.Close(false);
                        Open();
                    }
                }
                else if (IsOpen && keyboard.escapeKey.wasPressedThisFrame)
                {
                    Close(true);
                }
                else if (IsOpen && keyboard.spaceKey.wasPressedThisFrame)
                {
                    following = true;
                }
                else if (IsOpen && keyboard.tabKey.wasPressedThisFrame)
                {
                    ShowLayer((Layer)(((int)layer + 1) % LayerNames.Length));
                }
                else if (IsOpen && PlotKey(keyboard).wasPressedThisFrame)
                {
                    SetPlotMode(!plotMode);
                }
            }

            if (!IsOpen) return;

            if (landMessageUntil > 0f && Time.unscaledTime >= landMessageUntil)
            {
                landMessageUntil = 0f;
                landMessage.text = "";
            }

            if (Time.unscaledTime >= nextCensus)
            {
                nextCensus = Time.unscaledTime + CensusInterval;
                ecosystemSync.RequestCensus(speciesPanel.SelectedKind, speciesPanel.SelectedSpecies);
            }

            Place(worldMap.Texture, player.transform);
        }

        // Another species chosen (or none): its census is asked for at once.
        private void OnSpeciesChosen()
        {
            nextCensus = 0f;
            ShowLayerName();
        }

        // The key showing M on this keyboard's layout: Input System keys are named after their place
        // on a QWERTY keyboard, where M is not where it is on an AZERTY one.
        private static KeyControl MapKey(Keyboard keyboard)
        {
            return keyboard.FindKeyOnCurrentKeyboardLayout("m") ?? keyboard.mKey;
        }

        private static KeyControl PlotKey(Keyboard keyboard)
        {
            return keyboard.FindKeyOnCurrentKeyboardLayout("c") ?? keyboard.cKey;
        }

        // Plot mode: a click claims a free plot or releases one of the player's, and the land panel
        // (how many they hold, whom they allow) takes the species list's place.
        private void SetPlotMode(bool on)
        {
            plotMode = on && claimSync != null;
            speciesPanelRoot.EnableInClassList("hidden", plotMode);
            landPanel.EnableInClassList("hidden", !plotMode);
            claimHover.EnableInClassList("hidden", true);
            landPointer.text = "";
            landMessage.text = "";
            landMessageUntil = 0f;
            if (!plotMode) return;

            speciesPanel.Blur();
            claimSync.RequestAccessList();
            landShowWorld.SetValueWithoutNotify(ClaimOverlay.Shown);
            ShowLand();
        }

        // The land panel: the plots held out of the most, and the access list.
        private void ShowLand()
        {
            if (claimSync == null || landCount == null) return;

            landCount.text = Loc.T("{0} / {1} plots", claimSync.LocalCount, Voxel.Core.ClaimRules.MaxPlotsPerPlayer);
            AccessListView.Fill(landList, landEmpty, claimSync.Access, claimSync.RequestAccess);
        }

        private void OnPlotChanged(int plotX, int plotZ)
        {
            if (plotMode) landCount.text = Loc.T("{0} / {1} plots", claimSync.LocalCount, Voxel.Core.ClaimRules.MaxPlotsPerPlayer);
            if (pointerInside) ShowPointer(pointerLocal);
        }

        // A refusal while the map is open shows in the land panel (the hotbar is hidden).
        private void OnClaimRefused(string text)
        {
            if (!IsOpen) return;

            if (!plotMode) SetPlotMode(true);
            landMessage.text = text;
            landMessageUntil = Time.unscaledTime + LandMessageSeconds;
        }

        public void Open()
        {
            if (IsOpen) return;

            IsOpen = true;
            following = true;
            nextCensus = 0f;
            pixelsPerBlock = StartPixelsPerBlock;
            pointerText.text = "";
            SetPlotMode(false);
            screen.style.display = DisplayStyle.Flex;
            Cursor.lockState = CursorLockMode.None;
        }

        // relock: back to the game, the mouse steers the player again.
        private void Close(bool relock)
        {
            IsOpen = false;
            dragging = false;
            closedFrame = Time.frameCount;
            screen.style.display = DisplayStyle.None;
            if (relock) Cursor.lockState = CursorLockMode.Locked;
        }

        private void Place(Texture2D texture, Transform player)
        {
            Rect rect = view.contentRect;
            if (float.IsNaN(rect.width) || rect.width <= 0f || rect.height <= 0f) return;

            if (texture != shownTexture)
            {
                shownTexture = texture;
                image.style.backgroundImage = new StyleBackground(texture);
            }

            // Zoomed out, the whole world fits in the view.
            viewSize = rect.size;
            minPixelsPerBlock = Mathf.Min(MaxPixelsPerBlock, Mathf.Min(rect.width / texture.width, rect.height / texture.height));
            pixelsPerBlock = Mathf.Clamp(pixelsPerBlock, minPixelsPerBlock, MaxPixelsPerBlock);
            if (following) centre = new Vector2(player.position.x, player.position.z);
            centre.x = Mathf.Clamp(centre.x, 0f, texture.width);
            centre.y = Mathf.Clamp(centre.y, 0f, texture.height);

            // Less than a pixel per column: blended rather than skipping columns.
            texture.filterMode = pixelsPerBlock < 1f ? FilterMode.Bilinear : FilterMode.Point;
            image.style.width = texture.width * pixelsPerBlock;
            image.style.height = texture.height * pixelsPerBlock;
            var translate = new Translate(
                viewSize.x * 0.5f - centre.x * pixelsPerBlock,
                viewSize.y * 0.5f - (texture.height - centre.y) * pixelsPerBlock);
            image.style.translate = translate;

            // The climate layer covers the world exactly like the picture: one pixel per cell of 8 columns.
            if (layer != Layer.Terrain)
            {
                Texture2D layerTexture = LayerTexture(worldMap.World);
                if (layerTexture != shownLayerTexture)
                {
                    shownLayerTexture = layerTexture;
                    climate.style.backgroundImage = new StyleBackground(layerTexture);
                }

                climate.style.width = image.style.width;
                climate.style.height = image.style.height;
                climate.style.translate = translate;
            }

            // The species layer covers the world the same way, one pixel per census cell.
            ShowCensus();
            if (speciesLayer.style.display == DisplayStyle.Flex)
            {
                speciesLayer.style.width = image.style.width;
                speciesLayer.style.height = image.style.height;
                speciesLayer.style.translate = translate;
            }

            PlaceClaims(texture, translate);
            markers.Place(networkManager, ecosystemSync, worldMap, player, projection);
        }

        // The claimed plots cover the world like the picture (one pixel per column), and in plot mode
        // the plot under the pointer is framed.
        private void PlaceClaims(Texture2D texture, Translate translate)
        {
            Texture2D claims = claimSync != null && claimSync.Layer != null ? claimSync.Layer.Texture : null;
            if (claims != shownClaims)
            {
                shownClaims = claims;
                claimsLayer.style.backgroundImage = claims != null ? new StyleBackground(claims) : new StyleBackground();
            }

            claimsLayer.style.display = claims != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (claims != null)
            {
                claims.filterMode = pixelsPerBlock < 1f ? FilterMode.Bilinear : FilterMode.Point;
                claimsLayer.style.width = texture.width * pixelsPerBlock;
                claimsLayer.style.height = texture.height * pixelsPerBlock;
                claimsLayer.style.translate = translate;
            }

            bool hover = plotMode && pointerInside && !dragging;
            if (hover)
            {
                Vector2 column = Unproject(pointerLocal);
                int plotX = Voxel.Core.ClaimRules.PlotOf(Mathf.FloorToInt(column.x));
                int plotZ = Voxel.Core.ClaimRules.PlotOf(Mathf.FloorToInt(column.y));
                hover = plotX >= 0 && plotZ >= 0 && plotX < claimSync.PlotsX && plotZ < claimSync.PlotsZ;
                if (hover)
                {
                    float size = Voxel.Core.ClaimRules.PlotSize * pixelsPerBlock;
                    claimHover.style.left = viewSize.x * 0.5f + (plotX * Voxel.Core.ClaimRules.PlotSize - centre.x) * pixelsPerBlock;
                    claimHover.style.top = viewSize.y * 0.5f - ((plotZ + 1) * Voxel.Core.ClaimRules.PlotSize - centre.y) * pixelsPerBlock;
                    claimHover.style.width = size;
                    claimHover.style.height = size;
                }
            }

            claimHover.EnableInClassList("hidden", !hover);
        }

        private void ShowLayer(Layer shown)
        {
            layer = shown;
            climate.style.display = shown == Layer.Terrain ? DisplayStyle.None : DisplayStyle.Flex;
            ShowLayerName();
        }

        // The climate layer's name, then the chosen species and how many there are.
        private void ShowLayerName()
        {
            // The plain terrain goes unnamed: only a climate layer laid over it is.
            string climateName = layer == Layer.Terrain ? "" : Loc.T(LayerNames[(int)layer]);
            string speciesName = speciesPanel == null || speciesPanel.SelectedName == null
                ? ""
                : speciesPanel.SelectedKind == SpeciesCensus.Kind.Soil
                    ? Loc.T("{0} in the soil: {1} on average, deeper where richer", speciesPanel.SelectedName, speciesPanel.SelectedCount)
                    : Loc.T("{0}: {1}, yellow where they are most", speciesPanel.SelectedName, speciesPanel.SelectedCount);
            layerName.text = climateName.Length > 0 && speciesName.Length > 0 ? $"{climateName}   ·   {speciesName}" : climateName + speciesName;
        }

        // When a new census has come: the counts in the list, and the chosen species' layer if the
        // census is for it (the one for the species chosen before may still arrive first).
        private void ShowCensus()
        {
            SpeciesCensus census = ecosystemSync.Census;
            bool forChosen = speciesPanel.SelectedKind != SpeciesCensus.Kind.None
                && census.GridKind == speciesPanel.SelectedKind
                && census.GridSpecies == speciesPanel.SelectedSpecies
                && census.Grid.Length > 0;
            speciesLayer.style.display = forChosen ? DisplayStyle.Flex : DisplayStyle.None;
            if (census.Version == shownCensus) return;

            shownCensus = census.Version;
            speciesPanel.ShowCounts(census);
            ShowLayerName();
            if (!forChosen) return;

            if (speciesTexture == null || speciesTexture.width != census.CellsX || speciesTexture.height != census.CellsZ)
            {
                if (speciesTexture != null) Destroy(speciesTexture);
                speciesTexture = new Texture2D(census.CellsX, census.CellsZ, TextureFormat.RGBA32, false)
                {
                    name = "SpeciesLayer",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };
            }

            int most = 1;
            foreach (byte count in census.Grid)
            {
                if (count > most) most = count;
            }

            // A species: its density against the most crowded cell; the soil: the nutrient's level.
            bool soilLayer = census.GridKind == SpeciesCensus.Kind.Soil;
            var pixels = new Color32[census.Grid.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = soilLayer
                    ? ClimateColours.SoilLayer(census.GridSpecies, census.Grid[i] / SpeciesCensus.SoilLevelScale)
                    : ClimateColours.DensityLayer((float)census.Grid[i] / most);
            }

            speciesTexture.SetPixels32(pixels);
            speciesTexture.Apply(false);
            speciesLayer.style.backgroundImage = new StyleBackground(speciesTexture);
        }

        // The picture of the shown climate layer, worked out once per world.
        private Texture2D LayerTexture(Voxel.Core.World world)
        {
            if (world != layerWorld)
            {
                DropLayers();
                layerWorld = world;
            }

            Voxel.Core.Geography geography = world.Geography;
            if (layer == Layer.Temperature)
            {
                if (temperatureLayer == null) temperatureLayer = BuildLayer(geography, (cellX, cellZ) => ClimateColours.TemperatureLayer(geography.Temperature(cellX, cellZ)));
                return temperatureLayer;
            }

            if (humidityLayer == null) humidityLayer = BuildLayer(geography, (cellX, cellZ) => ClimateColours.HumidityLayer(geography.Humidity(cellX, cellZ)));
            return humidityLayer;
        }

        private static Texture2D BuildLayer(Voxel.Core.Geography geography, System.Func<int, int, Color> colourOf)
        {
            var texture = new Texture2D(geography.CellsX, geography.CellsZ, TextureFormat.RGBA32, false)
            {
                name = "ClimateLayer",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[geography.CellsX * geography.CellsZ];
            for (int cellZ = 0; cellZ < geography.CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < geography.CellsX; cellX++)
                {
                    pixels[cellX + geography.CellsX * cellZ] = colourOf(cellX, cellZ);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void DropLayers()
        {
            if (temperatureLayer != null) Destroy(temperatureLayer);
            if (humidityLayer != null) Destroy(humidityLayer);
            temperatureLayer = null;
            humidityLayer = null;
            layerWorld = null;
        }

        private bool Project(Vector3 world, out Vector2 point)
        {
            point = new Vector2(
                viewSize.x * 0.5f + (world.x - centre.x) * pixelsPerBlock,
                viewSize.y * 0.5f - (world.z - centre.y) * pixelsPerBlock);
            return point.x >= 0f && point.y >= 0f && point.x <= viewSize.x && point.y <= viewSize.y;
        }

        // The world column under a point of the view.
        private Vector2 Unproject(Vector2 local)
        {
            return new Vector2(
                centre.x + (local.x - viewSize.x * 0.5f) / pixelsPerBlock,
                centre.y - (local.y - viewSize.y * 0.5f) / pixelsPerBlock);
        }

        // Zooms around the pointer: the column under it stays under it.
        private void OnWheel(WheelEvent e)
        {
            if (viewSize.x <= 0f) return;

            Vector2 anchor = Unproject(e.localMousePosition);
            float zoom = e.delta.y > 0f ? 1f / ZoomStep : ZoomStep;
            pixelsPerBlock = Mathf.Clamp(pixelsPerBlock * zoom, minPixelsPerBlock, MaxPixelsPerBlock);
            centre = new Vector2(
                anchor.x - (e.localMousePosition.x - viewSize.x * 0.5f) / pixelsPerBlock,
                anchor.y + (e.localMousePosition.y - viewSize.y * 0.5f) / pixelsPerBlock);
            following = false;
            e.StopPropagation();
        }

        private void OnPointerDown(PointerDownEvent e)
        {
            // A right click teleports the player to the column under the pointer; the map follows them there.
            if (e.button == 1)
            {
                Vector2 column = Unproject(e.localPosition);
                Voxel.Core.World world = worldMap.World;
                int x = Mathf.FloorToInt(column.x);
                int z = Mathf.FloorToInt(column.y);
                if (world != null && world.InBounds(x, 0, z))
                {
                    commandSync.Run($"tp {x} {z}");
                    following = true;
                }

                return;
            }

            if (e.button != 0) return;

            pressAt = e.localPosition;
            dragging = true;
            dragStart = e.localPosition;
            dragCentre = centre;
            following = false;
            view.CapturePointer(e.pointerId);
        }

        private void OnPointerMove(PointerMoveEvent e)
        {
            if (dragging)
            {
                Vector2 moved = (Vector2)e.localPosition - dragStart;
                centre = new Vector2(dragCentre.x - moved.x / pixelsPerBlock, dragCentre.y + moved.y / pixelsPerBlock);
            }

            pointerInside = true;
            pointerLocal = e.localPosition;
            ShowPointer(e.localPosition);
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (e.button != 0 || !dragging) return;

            dragging = false;
            view.ReleasePointer(e.pointerId);

            // In plot mode a click (not a drag) claims or releases the plot under it.
            if (!plotMode || ((Vector2)e.localPosition - pressAt).magnitude > ClickThreshold) return;

            // The map moved with the press: back where it was clicked.
            centre = dragCentre;
            Vector2 column = Unproject(pressAt);
            claimSync.RequestToggle(Voxel.Core.ClaimRules.PlotOf(Mathf.FloorToInt(column.x)), Voxel.Core.ClaimRules.PlotOf(Mathf.FloorToInt(column.y)));
        }

        // The coordinates of the column under the pointer, the height of its ground, and its climate.
        private void ShowPointer(Vector2 local)
        {
            Voxel.Core.World world = worldMap.World;
            Vector2 column = Unproject(local);
            int x = Mathf.FloorToInt(column.x);
            int z = Mathf.FloorToInt(column.y);
            if (world == null || !world.InBounds(x, 0, z))
            {
                pointerText.text = "";
                return;
            }

            Voxel.Core.Geography geography = world.Geography;
            int celsius = ClimateColours.Celsius(geography.TemperatureAt(x, z));
            int humidity = ClimateColours.Percent(geography.HumidityAt(x, z));
            pointerText.text = $"X {x}   Y {world.GetSurfaceHeight(x, z)}   Z {z}      {celsius} °C   " + Loc.T("{0} % humidity", humidity) + PlotText(x, z);
        }

        // Whose plot the pointer is over, after the coordinates when it is claimed; in plot mode, with
        // what a click would do, in the land panel (too long for the footer).
        private string PlotText(int x, int z)
        {
            if (claimSync == null) return "";

            bool claimed = claimSync.TryDescribe(x, z, out string owner, out ClaimRelation relation);
            if (plotMode)
            {
                landPointer.text = !claimed
                    ? Loc.T("Free plot: click to claim it ({0} / {1})", claimSync.LocalCount, Voxel.Core.ClaimRules.MaxPlotsPerPlayer)
                    : relation == ClaimRelation.Yours
                        ? Loc.T("Your plot: click to release it")
                        : HudBanner.LandOf(owner, relation);
            }

            return claimed ? "      " + HudBanner.LandOf(owner, relation) : "";
        }

    }
}
