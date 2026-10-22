using System.Collections.Generic;
using UnityEngine;

namespace Voxel.Client
{
    // A flat-shaded mesh of axis-aligned boxes (centre, size), one submesh per list, so that each list
    // can take its own material: arrows (ArrowViews) and the tools in players' hands (PlayerModel).
    internal static class BoxMesh
    {
        private static readonly Vector3[] Directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

        public static Mesh Build(string name, IReadOnlyList<IReadOnlyList<(Vector3 centre, Vector3 size)>> submeshes)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
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
                        for (int k = 0; k < 4; k++) normals.Add(normal);
                        triangles[s].AddRange(new[] { first, first + 2, first + 1, first, first + 3, first + 2 });
                    }
                }
            }

            var mesh = new Mesh { name = name, subMeshCount = submeshes.Count };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            for (int s = 0; s < submeshes.Count; s++)
            {
                mesh.SetTriangles(triangles[s], s);
            }

            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
