using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // The chips a hit sends flying: small cubes of the colour of what was hit, thrown out of the face
    // that was struck and up, falling under gravity and coming to rest on the ground, then shrinking
    // away. Display only (each machine draws its own, at random), drawn instanced, one batch per burst
    // since each has its colour.
    internal sealed class ChipBursts
    {
        public const int ChipsPerHit = 6;
        public const int ChipsPerBreak = 14;

        private const int MostChips = 300;
        private const float Gravity = 18f;
        private const float ShrinkSeconds = 0.3f;

        private struct Chip
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Quaternion Spin;
            public float Size;
            public float Life;
            public float Age;
        }

        private sealed class Burst
        {
            public readonly List<Chip> Chips = new List<Chip>();
            public MaterialPropertyBlock Colour;
        }

        private static readonly int BaseColourId = Shader.PropertyToID("_BaseColor");

        private readonly Mesh cube;
        private readonly Material material;
        private readonly List<Burst> bursts = new List<Burst>();
        private readonly Stack<MaterialPropertyBlock> spare = new Stack<MaterialPropertyBlock>();
        private Matrix4x4[] matrices = new Matrix4x4[32];
        private int chipCount;

        public ChipBursts(Material material)
        {
            this.material = material;
            cube = BoxMesh.Build("Chip", new[] { new[] { (Vector3.zero, Vector3.one) } });
        }

        // count chips at point, thrown out along normal (and up), of colour; scale shrinks or grows
        // them (a broken arrow's splinters are smaller than a block's chips).
        public void Spawn(Vector3 point, Vector3 normal, Color colour, int count, float scale = 1f)
        {
            if (material == null) return;

            count = Mathf.Min(count, MostChips - chipCount);
            if (count <= 0) return;

            var burst = new Burst { Colour = spare.Count > 0 ? spare.Pop() : new MaterialPropertyBlock() };
            burst.Colour.SetColor(BaseColourId, colour);
            for (int i = 0; i < count; i++)
            {
                Vector3 sideways = Random.insideUnitSphere * 1.2f;
                burst.Chips.Add(new Chip
                {
                    Position = point + normal * 0.05f + Random.insideUnitSphere * 0.08f,
                    Velocity = normal * Random.Range(1f, 2.5f) + sideways + Vector3.up * 2f,
                    Spin = Random.rotation,
                    Size = Random.Range(0.06f, 0.12f) * scale,
                    Life = Random.Range(0.6f, 0.9f)
                });
            }

            chipCount += count;
            bursts.Add(burst);
        }

        // Moves and draws the chips; a chip entering a solid block comes to rest on it.
        public void Render(World world, float deltaTime)
        {
            for (int b = bursts.Count - 1; b >= 0; b--)
            {
                Burst burst = bursts[b];
                List<Chip> chips = burst.Chips;
                int drawn = 0;
                if (matrices.Length < chips.Count) matrices = new Matrix4x4[Mathf.NextPowerOfTwo(chips.Count)];

                for (int i = chips.Count - 1; i >= 0; i--)
                {
                    Chip chip = chips[i];
                    chip.Age += deltaTime;
                    if (chip.Age >= chip.Life)
                    {
                        chips.RemoveAt(i);
                        chipCount--;
                        continue;
                    }

                    chip.Velocity.y -= Gravity * deltaTime;
                    Vector3 next = chip.Position + chip.Velocity * deltaTime;
                    if (world != null && world.IsSolid(Mathf.FloorToInt(next.x), Mathf.FloorToInt(next.y), Mathf.FloorToInt(next.z)))
                    {
                        // On the ground: it stops falling and slides to a halt.
                        next = chip.Position;
                        chip.Velocity = new Vector3(chip.Velocity.x * 0.3f, 0f, chip.Velocity.z * 0.3f);
                    }

                    chip.Position = next;
                    chips[i] = chip;

                    float left = chip.Life - chip.Age;
                    float size = chip.Size * (left < ShrinkSeconds ? left / ShrinkSeconds : 1f);
                    matrices[drawn++] = Matrix4x4.TRS(chip.Position, chip.Spin, Vector3.one * size);
                }

                if (chips.Count == 0)
                {
                    spare.Push(burst.Colour);
                    bursts.RemoveAt(b);
                    continue;
                }

                var parameters = new RenderParams(material)
                {
                    matProps = burst.Colour,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = true,
                    worldBounds = new Bounds(chips[0].Position, Vector3.one * 8f)
                };
                Graphics.RenderMeshInstanced(parameters, cube, 0, matrices, drawn);
            }
        }

        public void Clear()
        {
            foreach (Burst burst in bursts)
            {
                spare.Push(burst.Colour);
            }

            bursts.Clear();
            chipCount = 0;
        }
    }
}
