using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Voxel.Client
{
    // A green halo around what a right click would pick up (an arrow, a carcass still worth taking),
    // so the player sees it can be taken. PlayerController fills Parts each frame with the meshes of
    // what it aims at, placed as they are drawn; they are drawn here again, a little inflated, back
    // faces only (ECO/PickupHalo): a wide faint glow, then a thin bright rim, gently pulsing. The
    // inflation follows each mesh's smoothed normals (a copy made once per mesh), so the boxes' faces,
    // flat-shaded, do not come apart at their edges.
    public sealed class PickupHalo : MonoBehaviour
    {
        [SerializeField] private Material haloMaterial;
        [SerializeField] private Color colour = new Color(0.35f, 1f, 0.4f, 1f);
        [SerializeField] private float rimWidth = 0.018f;
        [SerializeField] private float glowWidth = 0.055f;
        [SerializeField, Range(0f, 1f)] private float glowAlpha = 0.3f;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int WidthId = Shader.PropertyToID("_Width");

        private readonly List<(Mesh mesh, Matrix4x4 matrix)> parts = new List<(Mesh, Matrix4x4)>();
        private readonly Dictionary<Mesh, Mesh> smoothed = new Dictionary<Mesh, Mesh>();
        private Material rim;
        private Material glow;

        // What to surround this frame; emptied once drawn.
        internal List<(Mesh mesh, Matrix4x4 matrix)> Parts => parts;

        private void Awake()
        {
            if (Application.isBatchMode || haloMaterial == null) return;

            // The glow first, then the rim over it.
            glow = new Material(haloMaterial) { name = "Pickup glow", renderQueue = (int)RenderQueue.Transparent };
            glow.SetFloat(WidthId, glowWidth);
            rim = new Material(haloMaterial) { name = "Pickup rim", renderQueue = (int)RenderQueue.Transparent + 1 };
            rim.SetFloat(WidthId, rimWidth);
        }

        private void LateUpdate()
        {
            if (parts.Count == 0) return;

            if (rim != null)
            {
                float pulse = 0.8f + 0.2f * Mathf.Sin(Time.time * 5f);
                glow.SetColor(ColorId, new Color(colour.r, colour.g, colour.b, glowAlpha * pulse));
                rim.SetColor(ColorId, new Color(colour.r, colour.g, colour.b, pulse));
                Draw(glow);
                Draw(rim);
            }

            parts.Clear();
        }

        private void Draw(Material material)
        {
            var parameters = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            foreach ((Mesh mesh, Matrix4x4 matrix) in parts)
            {
                if (mesh != null) Graphics.RenderMesh(parameters, Smoothed(mesh), 0, matrix);
            }
        }

        // A copy of the mesh as one submesh, each vertex's normal the mean of those of every vertex
        // at the same place. A mesh whose data cannot be read is used as it is.
        private Mesh Smoothed(Mesh mesh)
        {
            if (smoothed.TryGetValue(mesh, out Mesh copy)) return copy;

            copy = mesh;
            if (mesh.isReadable)
            {
                Vector3[] vertices = mesh.vertices;
                Vector3[] normals = mesh.normals;
                var triangles = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++) triangles.AddRange(mesh.GetTriangles(s));

                var sums = new Dictionary<Vector3Int, Vector3>();
                if (normals.Length == vertices.Length)
                {
                    for (int i = 0; i < vertices.Length; i++) AddTo(sums, vertices[i], normals[i]);
                }
                else
                {
                    // No normals: those of the triangles.
                    for (int t = 0; t + 2 < triangles.Count; t += 3)
                    {
                        Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                        Vector3 face = Vector3.Cross(b - a, c - a).normalized;
                        AddTo(sums, a, face);
                        AddTo(sums, b, face);
                        AddTo(sums, c, face);
                    }
                }

                var smooth = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++) smooth[i] = sums.TryGetValue(KeyOf(vertices[i]), out Vector3 sum) ? sum.normalized : Vector3.zero;

                copy = new Mesh { name = mesh.name + " (halo)", indexFormat = mesh.indexFormat };
                copy.vertices = vertices;
                copy.normals = smooth;
                copy.SetTriangles(triangles, 0);
                copy.RecalculateBounds();
            }

            smoothed[mesh] = copy;
            return copy;
        }

        private static void AddTo(Dictionary<Vector3Int, Vector3> sums, Vector3 vertex, Vector3 normal)
        {
            Vector3Int key = KeyOf(vertex);
            sums.TryGetValue(key, out Vector3 sum);
            sums[key] = sum + normal;
        }

        // Vertices closer than a tenth of a millimetre count as the same place.
        private static Vector3Int KeyOf(Vector3 vertex) =>
            new Vector3Int(Mathf.RoundToInt(vertex.x * 10000f), Mathf.RoundToInt(vertex.y * 10000f), Mathf.RoundToInt(vertex.z * 10000f));

        private void OnDestroy()
        {
            foreach (KeyValuePair<Mesh, Mesh> pair in smoothed)
            {
                if (pair.Value != pair.Key) Destroy(pair.Value);
            }

            smoothed.Clear();
            if (rim != null) Destroy(rim);
            if (glow != null) Destroy(glow);
        }
    }
}
