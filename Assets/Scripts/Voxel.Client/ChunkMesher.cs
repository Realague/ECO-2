using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Simple meshing: one face per block side that can be seen. To call from the main thread.
    // Submesh 0 holds the opaque blocks, drawn with the block atlas; submesh 1 holds the water,
    // drawn with a transparent material. A block's face is drawn when the block next to it is not
    // opaque, so the bed and banks of a lake show through its water; a water face is drawn
    // against air or against open space, never against other water. The top of water that has air
    // above sits a little lower than a full block, like a surface.
    //
    // Each vertex carries the sky light (SkyLight) in the red channel of its colour, 0 to 1. On the
    // blocks' faces it is smooth: each corner takes the mean level of the four cells in front of
    // the face around that corner, leaving out opaque ones, and the blocks' shader darkens by it.
    internal static class ChunkMesher
    {
        public const int TopFace = 2;
        public const int BottomFace = 3;

        public const int SolidSubmesh = 0;
        public const int WaterSubmesh = 1;

        private const int FaceCount = 6;
        private const float UvInset = 0.01f;
        // Height of a water surface open to the air, above the bottom of its block.
        public const float WaterSurface = 0.875f;

        private static readonly Vector3Int[] FaceNormals =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1)
        };

        // The two axes along each face.
        private static readonly Vector3Int[] FaceAxisA =
        {
            new Vector3Int(0, 1, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 0),
            new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 0)
        };

        private static readonly Vector3Int[] FaceAxisB =
        {
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, 1),
            new Vector3Int(0, 1, 0), new Vector3Int(0, 1, 0)
        };

        // Four corners per face, clockwise seen from outside.
        // On the sides: bottom, top, top, bottom, so that the top of the tile stays up.
        private static readonly Vector3[][] FaceCorners =
        {
            new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) },
            new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) },
            new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) },
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) }
        };

        private static readonly Vector2[] CornerUvs =
        {
            new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0)
        };

        private static readonly List<Vector3> Vertices = new List<Vector3>();
        private static readonly List<Vector3> Normals = new List<Vector3>();
        private static readonly List<Vector2> Uvs = new List<Vector2>();
        private static readonly List<Color32> Colours = new List<Color32>();
        private static readonly List<int> SolidTriangles = new List<int>();
        private static readonly List<int> WaterTriangles = new List<int>();

        // The cells in front of a face, 3 × 3 around the one it touches, indexed (a + 1) + 3 * (b + 1)
        // along the face's two axes.
        private static readonly bool[] FrontOpaque = new bool[9];
        private static readonly int[] FrontLevel = new int[9];
        private static readonly float[] CornerLight = new float[4];

        public static void Build(World world, SkyLight light, Chunk chunk, Mesh mesh)
        {
            Vertices.Clear();
            Normals.Clear();
            Uvs.Clear();
            Colours.Clear();
            SolidTriangles.Clear();
            WaterTriangles.Clear();

            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;

            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        ushort block = chunk.Get(x, y, z);
                        ushort type = Blocks.TypeOf(block);
                        if (type == Blocks.Air) continue;

                        int worldX = originX + x;
                        int worldY = originY + y;
                        int worldZ = originZ + z;
                        if (type == Blocks.Water)
                        {
                            AddWater(world, light, worldX, worldY, worldZ, new Vector3(x, y, z));
                            continue;
                        }

                        for (int face = 0; face < FaceCount; face++)
                        {
                            Vector3Int normal = FaceNormals[face];
                            if (world.IsOpaque(worldX + normal.x, worldY + normal.y, worldZ + normal.z)) continue;

                            SampleFront(world, light, worldX + normal.x, worldY + normal.y, worldZ + normal.z, face);
                            AddFace(new Vector3(x, y, z), face, 1f, SolidTriangles, true);
                            AddTileUvs(BlockAtlas.TileFor(block, face));
                        }
                    }
                }
            }

            mesh.Clear();
            mesh.indexFormat = Vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(Vertices);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, Uvs);
            mesh.SetColors(Colours);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(SolidTriangles, SolidSubmesh);
            mesh.SetTriangles(WaterTriangles, WaterSubmesh);
        }

        private static void AddWater(World world, SkyLight light, int x, int y, int z, Vector3 blockOrigin)
        {
            // Water under air shows a surface a little below the top of its block.
            float top = world.IsWater(x, y + 1, z) ? 1f : WaterSurface;

            for (int face = 0; face < FaceCount; face++)
            {
                Vector3Int normal = FaceNormals[face];
                int nx = x + normal.x;
                int ny = y + normal.y;
                int nz = z + normal.z;
                if (world.IsOpaque(nx, ny, nz) || world.IsWater(nx, ny, nz)) continue;

                // A water face is lit by the air in front of it: open sky over a lake, darkness in a cave.
                float level = light.Get(nx, ny, nz) / (float)SkyLight.Max;
                for (int corner = 0; corner < 4; corner++)
                {
                    CornerLight[corner] = level;
                }

                AddFace(blockOrigin, face, top, WaterTriangles, false);
                for (int corner = 0; corner < 4; corner++)
                {
                    Uvs.Add(CornerUvs[corner]);
                }
            }
        }

        // Reads the cells in front of a face and works out the light of its four corners.
        private static void SampleFront(World world, SkyLight light, int x, int y, int z, int face)
        {
            Vector3Int axisA = FaceAxisA[face];
            Vector3Int axisB = FaceAxisB[face];
            for (int b = -1; b <= 1; b++)
            {
                for (int a = -1; a <= 1; a++)
                {
                    int cx = x + axisA.x * a + axisB.x * b;
                    int cy = y + axisA.y * a + axisB.y * b;
                    int cz = z + axisA.z * a + axisB.z * b;
                    int index = (a + 1) + 3 * (b + 1);
                    FrontOpaque[index] = world.IsOpaque(cx, cy, cz);
                    FrontLevel[index] = FrontOpaque[index] ? 0 : light.Get(cx, cy, cz);
                }
            }

            Vector3[] corners = FaceCorners[face];
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 position = corners[corner];
                int a = Vector3.Dot(position, axisA) > 0.5f ? 1 : -1;
                int b = Vector3.Dot(position, axisB) > 0.5f ? 1 : -1;

                int sideA = (a + 1) + 3;
                int sideB = 1 + 3 * (b + 1);
                int diagonal = (a + 1) + 3 * (b + 1);

                int sum = FrontLevel[4];
                int count = 1;
                if (!FrontOpaque[sideA])
                {
                    sum += FrontLevel[sideA];
                    count++;
                }

                if (!FrontOpaque[sideB])
                {
                    sum += FrontLevel[sideB];
                    count++;
                }

                // Light does not pass between two opaque blocks that touch by an edge.
                if (!FrontOpaque[diagonal] && !(FrontOpaque[sideA] && FrontOpaque[sideB]))
                {
                    sum += FrontLevel[diagonal];
                    count++;
                }

                CornerLight[corner] = sum / (count * (float)SkyLight.Max);
            }
        }

        // top: height of the block's top, 1 for a full block. The corners' light is in CornerLight.
        private static void AddFace(Vector3 blockOrigin, int face, float top, List<int> triangles, bool turnToLight)
        {
            int first = Vertices.Count;
            Vector3[] corners = FaceCorners[face];
            Vector3 normal = FaceNormals[face];

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 corner = corners[i];
                if (corner.y > 0f) corner.y = top;
                Vertices.Add(blockOrigin + corner);
                Normals.Add(normal);
                Colours.Add(new Color32((byte)Mathf.RoundToInt(CornerLight[i] * 255f), 0, 0, 255));
            }

            // The quad is split along the diagonal whose corners are brightest together, so that a
            // dark corner shades its own triangle instead of a band across the face.
            bool alongOtherDiagonal = turnToLight && CornerLight[0] + CornerLight[2] < CornerLight[1] + CornerLight[3];
            if (alongOtherDiagonal)
            {
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first + 3);
                triangles.Add(first + 1);
                triangles.Add(first + 3);
                triangles.Add(first);
            }
            else
            {
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first);
                triangles.Add(first + 2);
                triangles.Add(first + 3);
            }
        }

        private static void AddTileUvs(int tile)
        {
            for (int i = 0; i < 4; i++)
            {
                float u = Mathf.Lerp(UvInset, 1f - UvInset, CornerUvs[i].x);
                float v = Mathf.Lerp(UvInset, 1f - UvInset, CornerUvs[i].y);
                Uvs.Add(new Vector2((tile + u) / BlockAtlas.TileCount, v));
            }
        }
    }
}
