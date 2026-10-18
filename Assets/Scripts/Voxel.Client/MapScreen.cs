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
    // (WorldMap) with the same markers as the minimap. The mouse is freed while it is open: the
    // wheel zooms around the pointer, dragging moves the map, Space centres it on the player
    // again, a right click teleports the player there (the "tp" command, CommandSync), and the
    // coordinates under the pointer are shown below. Tab lays the climate over the
    // terrain: temperature, then humidity, from the world's geography. Meanwhile the player stands
    // still and the other on-screen texts are hidden. The screen is Assets/UI/Map.uxml.
    public class MapScreen : MonoBehaviour
    {
        private const float StartPixelsPerBlock = 3f;
        private const float MaxPixelsPerBlock = 12f;
        private const float ZoomStep = 1.25f;

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

        private VisualElement screen;
        private VisualElement view;
        private VisualElement image;
        private VisualElement climate;
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
            layerName = root.Q<Label>("layer-name");
            pointerText = root.Q<Label>("pointer-position");
            markers = new MapMarkers(root.Q("map-animals"), root.Q("map-players"), root.Q("map-arrow"));
            projection = Project;
            AddAnimalsToLegend(root.Q("legend"));

            view.RegisterCallback<WheelEvent>(OnWheel);
            view.RegisterCallback<PointerDownEvent>(OnPointerDown);
            view.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            view.RegisterCallback<PointerUpEvent>(OnPointerUp);
            view.RegisterCallback<PointerLeaveEvent>(_ => pointerText.text = "");
            screen.style.display = DisplayStyle.None;
            ShowLayer(Layer.Terrain);

            // A dedicated server has no one to show a map to.
            if (Application.isBatchMode) enabled = false;
        }

        private void OnDestroy()
        {
            DropLayers();
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

            // An M typed in the command line does not open the map.
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && !CommandConsole.HoldsInput)
            {
                if (MapKey(keyboard).wasPressedThisFrame)
                {
                    if (IsOpen) Close(true);
                    else Open();
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
            }

            if (IsOpen) Place(worldMap.Texture, player.transform);
        }

        // The key showing M on this keyboard's layout: Input System keys are named after their place
        // on a QWERTY keyboard, where M is not where it is on an AZERTY one.
        private static KeyControl MapKey(Keyboard keyboard)
        {
            return keyboard.FindKeyOnCurrentKeyboardLayout("m") ?? keyboard.mKey;
        }

        private void Open()
        {
            IsOpen = true;
            following = true;
            pixelsPerBlock = StartPixelsPerBlock;
            pointerText.text = "";
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

            markers.Place(networkManager, ecosystemSync, worldMap, player, projection);
        }

        private void ShowLayer(Layer shown)
        {
            layer = shown;
            layerName.text = LayerNames[(int)shown];
            climate.style.display = shown == Layer.Terrain ? DisplayStyle.None : DisplayStyle.Flex;
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

            ShowPointer(e.localPosition);
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            if (e.button != 0 || !dragging) return;

            dragging = false;
            view.ReleasePointer(e.pointerId);
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
            pointerText.text = $"X {x}   Y {world.GetSurfaceHeight(x, z)}   Z {z}      {celsius} °C   {humidity} % humidity";
        }

        // One legend entry per animal species, with its colour on the map and its name.
        private void AddAnimalsToLegend(VisualElement legend)
        {
            IReadOnlyList<AnimalSpeciesAsset> animals = ecosystemSync.AnimalAssets;
            for (int s = 0; s < animals.Count; s++)
            {
                var item = new VisualElement();
                item.AddToClassList("legend-item");

                var dot = new VisualElement();
                dot.AddToClassList("map-animal");
                dot.AddToClassList("legend-dot");
                dot.style.backgroundColor = worldMap.AnimalColour(s);
                item.Add(dot);

                var name = new Label(animals[s].name);
                name.AddToClassList("legend-text");
                item.Add(name);
                legend.Add(item);
            }
        }
    }
}
