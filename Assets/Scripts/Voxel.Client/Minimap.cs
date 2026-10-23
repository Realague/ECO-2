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

        [SerializeField] private NetworkManager networkManager;
        [SerializeField] private EcosystemSync ecosystemSync;
        [SerializeField] private WorldMap worldMap;
        [SerializeField] private AppearanceMenu appearanceMenu;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private UIDocument document;

        private VisualElement screen;
        private VisualElement frame;
        private VisualElement image;
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
            frame = root.Q("map-frame");
            image = root.Q("map-image");
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

            if (texture != shownTexture)
            {
                shownTexture = texture;
                image.style.backgroundImage = new StyleBackground(texture);
            }

            texture.filterMode = FilterMode.Point;
            Place(texture, player.transform);
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
            image.style.translate = new Translate(
                half - centre.x * pixelsPerBlock,
                half - (texture.height - centre.z) * pixelsPerBlock);

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
