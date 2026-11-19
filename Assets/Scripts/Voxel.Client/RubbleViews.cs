using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // How a piece of rubble lying on the ground looks: a rough stone about half a block across, a few
    // cubes of its block run together (each turned and tipped its own way; two shapes), in the block's
    // tiles with the blocks' material, fully lit, and the ore a rock held laid over them as on the
    // block (ChunkMesher's overlay channel). One mesh per block code and shape, made the first time it
    // is drawn. A piece is placed by the middle of its underside and the way it lies.
    internal sealed class RubbleViews
    {
        // The box a piece fills about its foot: what stops players, and what a click picks up.
        public const float HalfWidth = 0.27f;
        public const float Height = 0.4f;

        public const int ShapeCount = 2;

        // Each shape's cubes: its size, where its middle stands (x, z; its height follows from its
        // size), its turn about the vertical and its tilt, in degrees.
        private static readonly (float size, Vector2 at, float yaw, float tilt)[][] Shapes =
        {
            new[]
            {
                (0.36f, new Vector2(-0.04f, -0.02f), 17f, 8f),
                (0.26f, new Vector2(0.13f, 0.09f), 58f, -12f),
                (0.18f, new Vector2(-0.12f, 0.15f), -31f, 18f)
            },
            new[]
            {
                (0.32f, new Vector2(0.05f, -0.06f), -22f, -10f),
                (0.28f, new Vector2(-0.12f, 0.07f), 35f, 14f),
                (0.17f, new Vector2(0.14f, 0.15f), 70f, -22f)
            }
        };

        private readonly RenderParams look;
        private readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<Vector2> overlayUvs = new List<Vector2>();
        private readonly List<Color32> colours = new List<Color32>();
        private readonly List<int> triangles = new List<int>();

        public RubbleViews(Material blockMaterial)
        {
            look = new RenderParams(blockMaterial) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
        }

        // Draws a piece of rubble of this block, in this shape (any number: taken modulo ShapeCount),
        // this frame.
        public void Draw(Vector3 position, float yaw, ushort block, int shape)
        {
            if (look.material == null) return;

            Graphics.RenderMesh(look, MeshOf(block, shape), 0, MatrixOf(position, yaw));
        }

        // A piece's mesh placed as Draw draws it, for the pickup halo.
        public void CollectParts(Vector3 position, float yaw, ushort block, int shape, List<(Mesh mesh, Matrix4x4 matrix)> into)
        {
            into.Add((MeshOf(block, shape), MatrixOf(position, yaw)));
        }

        public void Destroy()
        {
            foreach (Mesh mesh in meshes.Values) Object.Destroy(mesh);
            meshes.Clear();
        }

        private static Matrix4x4 MatrixOf(Vector3 position, float yaw) => Matrix4x4.TRS(position, Quaternion.Euler(0f, yaw * Mathf.Rad2Deg, 0f), Vector3.one);

        // Where in its tile a cube's face takes its piece of the texture (the piece's lower corner).
        private static Vector2 PieceOf(int cube, int face, float size)
        {
            uint h = (uint)(cube * 7 + face + 1) * 0x9E3779B1u;
            h ^= h >> 15;
            float room = 1f - size;
            return new Vector2((h & 0xFF) / 255f * room, ((h >> 8) & 0xFF) / 255f * room);
        }

        private static float Inset(float t) => Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, t);

        private Mesh MeshOf(ushort block, int shape)
        {
            shape = (shape % ShapeCount + ShapeCount) % ShapeCount;
            int key = block * ShapeCount + shape;
            if (meshes.TryGetValue(key, out Mesh mesh)) return mesh;

            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            overlayUvs.Clear();
            colours.Clear();
            triangles.Clear();

            ushort type = Blocks.TypeOf(block);
            OreKind ore = Ores.OreOf(block);
            TileVariants overlays = ore != OreKind.None ? BlockAtlas.Overlays : null;
            (float size, Vector2 at, float yaw, float tilt)[] cubes = Shapes[shape];
            for (int c = 0; c < cubes.Length; c++)
            {
                (float size, Vector2 at, float yaw, float tilt) = cubes[c];
                Quaternion turn = Quaternion.Euler(tilt, yaw, tilt * 0.5f);
                int cube = shape * 8 + c;

                // Sunk a little, so that a tipped corner does not leave it hovering.
                var middle = new Vector3(at.x, size * 0.42f, at.y);
                for (int face = 0; face < ChunkMesher.FaceCount; face++)
                {
                    int first = vertices.Count;
                    Vector3[] corners = ChunkMesher.FaceCorners[face];
                    Vector3 normal = turn * (Vector3)ChunkMesher.FaceNormals[face];
                    int tile = BlockAtlas.TileFor(type, face);
                    int overlaySlot = 0, overlayTurn = 0;
                    bool mirror = false;
                    if (overlays != null) overlaySlot = overlays.Pick((int)ore - 1, cube, 0, 0, face + ChunkMesher.FaceCount, out overlayTurn, out mirror, true);

                    // A piece of the tile as large as the cube is, from a corner picked per cube and
                    // face, so that its pixels are the block's size.
                    Vector2 from = PieceOf(cube, face, size);
                    for (int i = 0; i < corners.Length; i++)
                    {
                        vertices.Add(middle + turn * ((corners[i] - new Vector3(0.5f, 0.5f, 0.5f)) * size));
                        normals.Add(normal);

                        // Sky light full, no torch light; blue marks the ore's overlay for the shader.
                        colours.Add(new Color32(255, 0, (byte)(overlays != null ? 255 : 0), 255));
                        Vector2 corner = ChunkMesher.CornerUvs[i];
                        uvs.Add(new Vector2((tile + Inset(from.x + corner.x * size)) / BlockAtlas.SlotCount, Inset(from.y + corner.y * size)));
                        if (overlays != null)
                        {
                            Vector2 turned = ChunkMesher.CornerUvs[(i + overlayTurn) & 3];
                            float u = Inset(from.x + (mirror ? 1f - turned.x : turned.x) * size);
                            float v = Inset(from.y + turned.y * size);
                            overlayUvs.Add(new Vector2((overlaySlot + u) / overlays.SlotCount, v));
                        }
                    }

                    triangles.Add(first);
                    triangles.Add(first + 1);
                    triangles.Add(first + 2);
                    triangles.Add(first);
                    triangles.Add(first + 2);
                    triangles.Add(first + 3);
                }
            }

            mesh = new Mesh { name = "Rubble" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (overlays != null) mesh.SetUVs(ChunkMesher.OverlayUvChannel, overlayUvs);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            meshes.Add(key, mesh);
            return mesh;
        }
    }
}
