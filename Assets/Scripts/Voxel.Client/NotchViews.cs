using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // The notch an axe cuts in a tree as it is hit: a wedge on the side the blows come from, its floor
    // where the stump will be cut and its upper face sloping at 45°, as deep as the share of the hits
    // already taken times NotchDepth of the trunk's thickness. It is drawn as pale wood laid just over
    // the bark and lit like it (the trunk itself is one instanced mesh, which cannot be hollowed): on
    // the front, the wedge's opening, its floor light under a dark line at its back and its sloping
    // face in shade above (painted so); on the trunk's two sides, its profile. The wood is the
    // stump's cut material over a grain painted here at start.
    internal sealed class NotchViews
    {
        // The deepest a notch goes, as a share of the trunk's thickness (when the last hit lands).
        private const float NotchDepth = 0.6f;

        // How far over the bark the notch is laid, so that it never fights with it.
        private const float Lift = 0.008f;

        // The share of the opening the floor takes on the front, under the sloping face (the height of
        // the back of the notch in its texture).
        private const float FloorShare = 0.3f;

        private const int TextureSize = 32;

        // Texture repeats per block across the trunk: as fine as the plants' own textures.
        private const float GrainPerBlock = 2f;

        private readonly Texture2D grain;
        private readonly Dictionary<Material, RenderParams> looks = new Dictionary<Material, RenderParams>();
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<int> triangles = new List<int>();
        private int used;

        public NotchViews()
        {
            grain = PaintGrain();
        }

        // To call at the start of each frame's drawing: the meshes of last frame's notches are reused.
        public void BeginFrame()
        {
            used = 0;
        }

        // Draws a notch this frame. centre is the middle of the trunk at the floor's height, halfSize its
        // half extents along x and z, toward the way the blows come from (horizontal), progress the
        // share of the hits taken, wood the stump's cut material.
        public void Draw(Material wood, Vector3 centre, Vector2 halfSize, Vector3 toward, float progress)
        {
            if (progress <= 0f || wood == null) return;

            // The trunk's face toward the hitter: n out of it, r across it.
            Vector3 n, r;
            float depthHalf, widthHalf;
            if (Mathf.Abs(toward.x) >= Mathf.Abs(toward.z))
            {
                n = new Vector3(toward.x >= 0f ? 1f : -1f, 0f, 0f);
                r = Vector3.forward;
                depthHalf = halfSize.x;
                widthHalf = halfSize.y;
            }
            else
            {
                n = new Vector3(0f, 0f, toward.z >= 0f ? 1f : -1f);
                r = Vector3.right;
                depthHalf = halfSize.y;
                widthHalf = halfSize.x;
            }

            float depth = Mathf.Clamp01(progress) * NotchDepth * 2f * depthHalf;
            if (depth < 0.01f) return;

            float height = depth;
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            triangles.Clear();

            // The opening, on the front, v from its lower lip to its upper one (the texture shades the
            // floor, the line at the back and the sloping face); lit like the bark it lies on.
            Vector3 front = centre + n * (depthHalf + Lift);
            Vector3 across = r * (widthHalf + Lift);
            float u = (widthHalf + Lift) * GrainPerBlock;
            Vector3 top = front + Vector3.up * height;
            AddQuad(front - across, front + across, top + across, top - across, n, u, 0f, 1f);

            // The profile on each side: from the front edge, in to the back of the notch at its floor.
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 lip = centre + r * (side * (widthHalf + Lift)) + n * depthHalf;
                Vector3 apex = lip - n * depth;
                float uApex = depth * GrainPerBlock;
                AddTriangle(lip, lip + Vector3.up * height, apex, r * side, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(uApex, FloorShare));
            }

            Mesh mesh = NextMesh();
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            Graphics.RenderMesh(LookOf(wood), mesh, 0, Matrix4x4.identity);
        }

        public void Clear()
        {
            foreach (Mesh mesh in meshes)
            {
                Object.Destroy(mesh);
            }

            meshes.Clear();
            used = 0;
        }

        private Mesh NextMesh()
        {
            if (used == meshes.Count)
            {
                var created = new Mesh { name = "Notch" };
                created.MarkDynamic();
                meshes.Add(created);
            }

            Mesh mesh = meshes[used++];
            mesh.Clear();
            return mesh;
        }

        // The cut material with the grain, made once per material.
        private RenderParams LookOf(Material wood)
        {
            if (!looks.TryGetValue(wood, out RenderParams look))
            {
                var material = new Material(wood) { name = wood.name + " Notch", mainTexture = grain };
                look = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = true };
                looks.Add(wood, look);
            }

            return look;
        }

        // a, b, c, d around a quad; v runs from va along a-b to vb along d-c.
        private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, float uHalf, float va, float vb)
        {
            AddTriangle(a, b, c, normal, new Vector2(-uHalf, va), new Vector2(uHalf, va), new Vector2(uHalf, vb));
            AddTriangle(a, c, d, normal, new Vector2(-uHalf, va), new Vector2(uHalf, vb), new Vector2(-uHalf, vb));
        }

        // A triangle facing the way of normal, whatever the order it is given in.
        private void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
            {
                (b, c) = (c, b);
                (ub, uc) = (uc, ub);
            }

            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            uvs.Add(ua);
            uvs.Add(ub);
            uvs.Add(uc);
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
        }

        // From the bottom (v = 0): the floor, light; at FloorShare the back of the notch, dark; then the
        // sloping face, in shade. Streaks of grain run across (along u), with a few darker pixels.
        // Grey: the cut material's colour tints it.
        private static Texture2D PaintGrain()
        {
            var pixels = new Color32[TextureSize * TextureSize];
            var random = new System.Random(20261022);
            float backRow = FloorShare * TextureSize - 0.5f;
            for (int y = 0; y < TextureSize; y++)
            {
                int rowTone = (y < backRow ? 226 : 150) + random.Next(0, 3) * 10;
                for (int x = 0; x < TextureSize; x++)
                {
                    int tone = rowTone + random.Next(-6, 7);
                    if (random.Next(0, 23) == 0) tone -= 36;

                    float fromBack = Mathf.Abs(y - backRow);
                    if (fromBack < 3f) tone = Mathf.Min(tone, (int)(90 + 30 * fromBack));

                    byte value = (byte)Mathf.Clamp(tone, 0, 255);
                    pixels[x + TextureSize * y] = new Color32(value, value, value, 255);
                }
            }

            var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            {
                name = "Notch grain",
                filterMode = FilterMode.Point,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }
    }
}
