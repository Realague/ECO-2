using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // A round map in the corner of the screen, north up, centred on the local player: the world's
    // picture (WorldMap) slid and scaled under a round frame, the nearby animals, the other players
    // and the local player's arrow. It is hidden while the full-screen map or the inventory is open. The screen is
    // Assets/UI/Minimap.uxml.
    public class Minimap : MonoBehaviour
    {
        // Blocks from the centre of the map to its rim.
        private const float Radius = 64f;

        // The frame's width and border radius at scale 1 (Minimap.uss), and the north label's
        // width; MinimapSettings.Scale multiplies the first two.
        private const float FrameSize = 300f;
        private const float NorthSize = 30f;

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldMap worldMap;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private UIDocument document;

        private VisualElement screen;
        private VisualElement minimap;
        private VisualElement frame;
        private VisualElement north;
        private float shownScale = float.NaN;
        private VisualElement image;
        private VisualElement claims;
        private Texture2D shownClaims;
        private Label position;
        private MapMarkers markers;
        private Texture2D shownTexture;
        private MapMarkers.Projection projection;

        // The current placement: the player's position, the frame's half size and the scale.
        private Vector3 centre;
        private float half;
        private float pixelsPerBlock;

        // After every OnEnable, so that the document has built its elements.
        private void Start()
        {
            VisualElement root = document.rootVisualElement;
            screen = root.Q("screen");
            minimap = root.Q("minimap");
            frame = root.Q("map-frame");
            north = root.Q("map-north");
            image = root.Q("map-image");
            claims = root.Q("map-claims");
            position = root.Q<Label>("map-position");
            markers = new MapMarkers(root.Q("map-animals"), root.Q("map-players"), root.Q("map-arrow"));
            projection = Project;
            screen.style.display = DisplayStyle.None;

            // A dedicated server has no one to show a map to.
            if (Application.isBatchMode) enabled = false;
        }

        private void Update()
        {
            Texture2D texture = worldMap.Texture;
            NetworkObject player = texture != null && networkManager.IsListening && !appearanceMenu.IsOpen && !mapScreen.IsOpen && !inventoryScreen.IsOpen && !StorageScreen.IsOpen
                ? networkManager.SpawnManager?.GetLocalPlayerObject()
                : null;
            screen.style.display = player != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (player == null) return;

            Resize();
            if (texture != shownTexture)
            {
                shownTexture = texture;
                image.style.backgroundImage = new StyleBackground(texture);
            }

            texture.filterMode = FilterMode.Point;
            Place(texture, player.transform);
        }

        // The size chosen in the options: the frame grows or shrinks, still round and showing the
        // same blocks; the rim, the north label and the coordinates keep their size.
        private void Resize()
        {
            float scale = MinimapSettings.Scale;
            if (scale == shownScale) return;

            shownScale = scale;
            float size = Mathf.Round(FrameSize * scale);
            minimap.style.width = size;
            frame.style.width = size;
            frame.style.height = size;
            var radius = new Length(size * 0.5f);
            frame.style.borderTopLeftRadius = radius;
            frame.style.borderTopRightRadius = radius;
            frame.style.borderBottomLeftRadius = radius;
            frame.style.borderBottomRightRadius = radius;
            north.style.left = (size - NorthSize) * 0.5f;
        }

        private void Place(Texture2D texture, Transform player)
        {
            float size = frame.contentRect.width;
            if (float.IsNaN(size) || size <= 0f) return;

            centre = player.position;
            half = size * 0.5f;
            pixelsPerBlock = size / (2f * Radius);
            image.style.width = texture.width * pixelsPerBlock;
            image.style.height = texture.height * pixelsPerBlock;
            var translate = new Translate(
                half - centre.x * pixelsPerBlock,
                half - (texture.height - centre.z) * pixelsPerBlock);
            image.style.translate = translate;

            // The claimed plots, over the picture the same way (one pixel per column).
            Texture2D plots = claimSync != null && claimSync.Layer != null ? claimSync.Layer.Texture : null;
            if (plots != shownClaims)
            {
                shownClaims = plots;
                claims.style.backgroundImage = plots != null ? new StyleBackground(plots) : new StyleBackground();
            }

            claims.style.display = plots != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (plots != null)
            {
                plots.filterMode = FilterMode.Point;
                claims.style.width = image.style.width;
                claims.style.height = image.style.height;
                claims.style.translate = translate;
            }

            position.text = $"X {Mathf.FloorToInt(centre.x)}   Y {Mathf.FloorToInt(centre.y)}   Z {Mathf.FloorToInt(centre.z)}";

            markers.Place(networkManager, ecosystemSync, worldMap, player, projection);
        }

        // Inside the map's circle only.
        private bool Project(Vector3 world, out Vector2 point)
        {
            float dx = world.x - centre.x;
            float dz = world.z - centre.z;
            point = new Vector2(half + dx * pixelsPerBlock, half - dz * pixelsPerBlock);
            return dx * dx + dz * dz <= (Radius - 1f) * (Radius - 1f);
        }
    }
}
