using System.Collections.Generic;
using UnityEngine;

namespace Voxel.Client
{
    // A flat-shaded mesh of axis-aligned boxes (centre, size), one submesh per list, so that each list
    // can take its own material: arrows (ArrowViews), the tools in players' hands (PlayerModel), logs
    // (LogViews) and cracks (CrackViews). With uvs, each face's texture coordinates are its position
    // in blocks along its two axes, so a texture made of one tile per block runs on across faces.
    internal static class BoxMesh
    {
        private static readonly Vector3[] Directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        public static Mesh Build(string name, IReadOnlyList<IReadOnlyList<(Vector3 centre, Vector3 size)>> submeshes, bool uvs = false)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var coordinates = new List<Vector2>();
            var triangles = new List<int>[submeshes.Count];
            for (int s = 0; s < submeshes.Count; s++)
            {
                triangles[s] = new List<int>();
                foreach ((Vector3 centre, Vector3 size) in submeshes[s])
                {
                    Vector3 half = size * 0.5f;
                    foreach (Vector3 normal in Directions)
                    {
                        // Two axes across the face; its corners go round anticlockwise seen from outside,
                        // so the triangles take them in the other order (Unity's front faces wind clockwise).
                        Vector3 u = normal.x != 0f ? Vector3.forward : Vector3.right;
                        Vector3 v = Vector3.Cross(normal, u);
                        int first = vertices.Count;
                        Vector3 middle = centre + Vector3.Scale(normal, half);
                        Vector3 du = Vector3.Scale(u, half);
                        Vector3 dv = Vector3.Scale(v, half);
                        vertices.Add(middle - du - dv);
                        vertices.Add(middle - du + dv);
                        vertices.Add(middle + du + dv);
                        vertices.Add(middle + du - dv);
                        for (int k = 0; k < 4; k++)
                        {
                            normals.Add(normal);
                            Vector3 corner = vertices[first + k];
                            coordinates.Add(new Vector2(Vector3.Dot(corner, u), Vector3.Dot(corner, v)));
                        }
                        triangles[s].AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
                    }
                }
            }

            var mesh = new Mesh { name = name, subMeshCount = submeshes.Count };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            if (uvs) mesh.SetUVs(0, coordinates);
            for (int s = 0; s < submeshes.Count; s++)
            {
                mesh.SetTriangles(triangles[s], s);
            }

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
