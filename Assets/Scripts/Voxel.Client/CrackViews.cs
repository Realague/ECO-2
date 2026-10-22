using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // The cracks of a block being mined (MiningRules.CrackStages stages): dark lines drawn on a cube a
    // little larger than the block, cut out of a texture whose stages each add branches to the last.
    // The textures are painted here when the game starts, the same on every machine; the material
    // (an asset, alpha-clipped, so that its shader goes into builds) only gives the look.
    internal sealed class CrackViews
    {
        private const int TextureSize = 32;
        private const float Oversize = 1.006f;

        private readonly Mesh cube;
        private readonly RenderParams[] stages;

        public CrackViews(Material material)
        {
            cube = BoxMesh.Build("Cracks", new[] { new[] { (Vector3.zero, Vector3.one) } }, true);
            stages = new RenderParams[MiningRules.CrackStages];
            Texture2D[] textures = PaintStages();
            for (int s = 0; s < stages.Length; s++)
            {
                var stage = new Material(material) { name = $"Cracks {s + 1}", mainTexture = textures[s] };
                stages[s] = new RenderParams(stage) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            }
        }

        // Draws the cracks of a block this frame, at stage 1 to CrackStages.
        public void Draw(BlockPos pos, int stage)
        {
            if (stage < 1) return;

            stage = Mathf.Min(stage, stages.Length);
            var centre = new Vector3(pos.X + 0.5f, pos.Y + 0.5f, pos.Z + 0.5f);
            Graphics.RenderMesh(stages[stage - 1], cube, 0, Matrix4x4.TRS(centre, Quaternion.identity, Vector3.one * Oversize));
        }

        // Each stage draws on the one before: a few cracks from near the middle, each wandering out
        // and sometimes forking. Black with alpha where the lines are, transparent elsewhere.
        private static Texture2D[] PaintStages()
        {
            var pixels = new Color32[TextureSize * TextureSize];
            var random = new System.Random(20240613);
            var textures = new Texture2D[MiningRules.CrackStages];
            for (int s = 0; s < textures.Length; s++)
            {
                int cracks = 2 + s;
                for (int c = 0; c < cracks; c++)
                {
                    float x = TextureSize * 0.5f + (float)(random.NextDouble() - 0.5) * TextureSize * 0.4f;
                    float y = TextureSize * 0.5f + (float)(random.NextDouble() - 0.5) * TextureSize * 0.4f;
                    float angle = (float)(random.NextDouble() * Mathf.PI * 2.0);
                    Walk(pixels, random, x, y, angle, 6 + 3 * s, 2);
                }

                var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
                {
                    name = $"Cracks {s + 1}",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat
                };
                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                textures[s] = texture;
            }

            return textures;
        }

        private static void Walk(Color32[] pixels, System.Random random, float x, float y, float angle, int length, int forks)
        {
            for (int step = 0; step < length; step++)
            {
                int px = ((int)x % TextureSize + TextureSize) % TextureSize;
                int py = ((int)y % TextureSize + TextureSize) % TextureSize;
                pixels[px + TextureSize * py] = new Color32(20, 16, 12, 255);

                angle += (float)(random.NextDouble() - 0.5) * 0.9f;
                x += Mathf.Cos(angle);
                y += Mathf.Sin(angle);
                if (forks > 0 && random.NextDouble() < 0.12)
                {
                    Walk(pixels, random, x, y, angle + (random.NextDouble() < 0.5 ? 0.9f : -0.9f), (length - step) / 2, forks - 1);
                }
            }
        }
    }
}
