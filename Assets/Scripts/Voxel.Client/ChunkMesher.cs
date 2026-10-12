using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Maillage simple : une face par côté de bloc exposé à l'air. À appeler depuis le thread principal.
    internal static class ChunkMesher
    {
        public const int TopFace = 2;
        public const int BottomFace = 3;

        private const int FaceCount = 6;
        private const float UvInset = 0.01f;

        private static readonly Vector3Int[] FaceNormals =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1)
        };

        // Quatre coins par face, dans le sens horaire vu de l'extérieur.
        // Sur les côtés : bas, haut, haut, bas, pour que le haut de la tuile reste en haut.
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
        private static readonly List<int> Triangles = new List<int>();

        public static void Build(World world, Chunk chunk, Mesh mesh)
        {
            Vertices.Clear();
            Normals.Clear();
            Uvs.Clear();
            Triangles.Clear();

            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;

            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        byte block = chunk.Get(x, y, z);
                        if (block == Blocks.Air) continue;

                        for (int face = 0; face < FaceCount; face++)
                        {
                            Vector3Int normal = FaceNormals[face];
                            if (world.IsSolid(originX + x + normal.x, originY + y + normal.y, originZ + z + normal.z)) continue;

                            AddFace(new Vector3(x, y, z), face, BlockAtlas.TileFor(block, face));
                        }
                    }
                }
            }

            mesh.Clear();
            mesh.indexFormat = Vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(Vertices);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, Uvs);
            mesh.SetTriangles(Triangles, 0);
        }

        private static void AddFace(Vector3 blockOrigin, int face, int tile)
        {
            int first = Vertices.Count;
            Vector3[] corners = FaceCorners[face];
            Vector3 normal = FaceNormals[face];

            for (int i = 0; i < corners.Length; i++)
            {
                Vertices.Add(blockOrigin + corners[i]);
                Normals.Add(normal);

                float u = Mathf.Lerp(UvInset, 1f - UvInset, CornerUvs[i].x);
                float v = Mathf.Lerp(UvInset, 1f - UvInset, CornerUvs[i].y);
                Uvs.Add(new Vector2((tile + u) / BlockAtlas.TileCount, v));
            }

            Triangles.Add(first);
            Triangles.Add(first + 1);
            Triangles.Add(first + 2);
            Triangles.Add(first);
            Triangles.Add(first + 2);
            Triangles.Add(first + 3);
        }
    }
}
