using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Gives the blocks' shader the colour of the climate (ECO/Blocks, BlocksForwardPass.hlsl): a
    // texture of one pixel per cell of the world's geography, sampled with blending between cells
    // so that grass changes colour smoothly. Each machine works it out from the seed and size of the
    // world it has; nothing is sent. Before a world is there, grass has the colour of a mild climate.
    public class ClimateTint : MonoBehaviour
    {
        private static readonly int TintId = Shader.PropertyToID("_ClimateTint");
        private static readonly int TransformId = Shader.PropertyToID("_ClimateTintTransform");

        [SerializeField] private WorldSync worldSync;

        private Texture2D neutral;
        private Texture2D texture;
        private World shownWorld;

        private void Awake()
        {
            neutral = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "ClimateTint (mild)" };
            neutral.SetPixel(0, 0, ClimateColours.MildGrass);
            neutral.Apply(false, true);
            Publish(neutral, Vector4.zero);
        }

        private void OnDestroy()
        {
            if (texture != null) Destroy(texture);
            if (neutral != null) Destroy(neutral);
        }

        private void Update()
        {
            World world = worldSync.World;
            if (world == shownWorld) return;

            shownWorld = world;
            if (texture != null) Destroy(texture);
            texture = null;

            if (world == null)
            {
                Publish(neutral, Vector4.zero);
                return;
            }

            Geography geography = world.Geography;
            texture = new Texture2D(geography.CellsX, geography.CellsZ, TextureFormat.RGBA32, false)
            {
                name = "ClimateTint",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            var pixels = new Color32[geography.CellsX * geography.CellsZ];
            for (int cellZ = 0; cellZ < geography.CellsZ; cellZ++)
            {
                for (int cellX = 0; cellX < geography.CellsX; cellX++)
                {
                    pixels[cellX + geography.CellsX * cellZ] =
                        ClimateColours.Grass(geography.Temperature(cellX, cellZ), geography.Humidity(cellX, cellZ));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            // Pixel i covers the columns of cell i: its centre is the cell's centre.
            Publish(texture, new Vector4(
                1f / (geography.CellsX * Geography.CellSize), 1f / (geography.CellsZ * Geography.CellSize), 0f, 0f));
        }

        private static void Publish(Texture2D tint, Vector4 transform)
        {
            Shader.SetGlobalTexture(TintId, tint);
            Shader.SetGlobalVector(TransformId, transform);
        }
    }
}
