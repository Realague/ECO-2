using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // While the bow is drawn, the way the arrow would fly if let go now: a line along the same flight
    // as the shot (ArrowFlight.Trace, from the same eye, the same way, at the draw reached so far), up
    // to the block it would stop on, where a small square marks the spot. Seen from the eye, that arc
    // lies in the plane of the gaze and would show as a straight line, so the line starts from the bow
    // (low and to the right of the view, BowOffset) and joins the true flight over BlendDistance
    // blocks: past that it is exact. Its width grows with the distance from the eye, so that it looks
    // about as thick near and far. Display only, on the shooter's machine; it does not foresee
    // animals in the way.
    internal sealed class ArrowPreview
    {
        // The line starts this far along the flight and keeps one point in this many of its steps.
        private const float StartDistance = 0.4f;
        private const int PointStride = 2;

        // Where the line starts from, in the eye's space (right, up), and how far it takes to join
        // the flight.
        private static readonly Vector2 BowOffset = new Vector2(0.28f, -0.3f);
        private const float BlendDistance = 7f;

        // Width of the line and of the mark for each block away from the eye (about 3 and 14 pixels
        // on a 1080p screen), the mark at least MarkMinSize.
        private const float WidthPerBlock = 0.0035f;
        private const float MarkPerBlock = 0.016f;
        private const float MarkMinSize = 0.15f;

        private readonly LineRenderer line;
        private readonly Material material;
        private readonly Mesh mark;
        private readonly List<System.Numerics.Vector3> path = new List<System.Numerics.Vector3>();
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<float> along = new List<float>();
        private readonly AnimationCurve widths = new AnimationCurve();
        private bool hasMark;
        private Matrix4x4 markMatrix;

        public ArrowPreview(Material material)
        {
            this.material = new Material(material) { name = "Arrow preview" };
            var holder = new GameObject("Arrow preview") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(holder);
            line = holder.AddComponent<LineRenderer>();
            line.sharedMaterial = this.material;
            line.useWorldSpace = true;
            line.widthMultiplier = 1f;
            line.numCapVertices = 0;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.colorGradient = new Gradient
            {
                colorKeys = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                alphaKeys = new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(1f, 1f) }
            };
            line.enabled = false;
            mark = BoxMesh.Build("Arrow preview mark", new[] { new[] { (Vector3.zero, new Vector3(1f, 0.05f, 1f)) } });
        }

        // Shows the flight shot from the eye (its position, the way it looks) at this draw (0 to 1) in world.
        public void Show(World world, Transform eye, float power)
        {
            Vector3 origin = eye.position;
            path.Clear();
            bool stopped = ArrowFlight.Trace(world, origin.ToNumerics(), ArrowFlight.VelocityOf(eye.forward.ToNumerics(), power), out System.Numerics.Vector3 landing,
                out _, out VoxelHit hit, path);

            Vector3 offset = eye.right * BowOffset.x + eye.up * BowOffset.y;
            points.Clear();
            along.Clear();
            float length = 0f;
            for (int i = 0; i < path.Count; i++)
            {
                Vector3 point = path[i].ToUnity();
                bool last = i == path.Count - 1;
                float distance = (point - origin).magnitude;
                if (!last && (i % PointStride != 0 || distance < StartDistance)) continue;

                // From the bow, easing into the true flight.
                float blend = Mathf.Clamp01(1f - distance / BlendDistance);
                point += offset * (blend * blend * (3f - 2f * blend));
                if (points.Count > 0) length += (point - points[points.Count - 1]).magnitude;
                points.Add(point);
                along.Add(length);
            }

            if (points.Count < 2 || length <= 0f)
            {
                Hide();
                return;
            }

            // Each point as wide as its distance from the eye calls for (the curve runs along the
            // line's length, from 0 to 1).
            line.positionCount = points.Count;
            var keys = new Keyframe[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                line.SetPosition(i, points[i]);
                keys[i] = new Keyframe(along[i] / length, Vector3.Distance(points[i], origin) * WidthPerBlock);
            }

            widths.keys = keys;
            line.widthCurve = widths;
            line.enabled = true;

            // A thin square lying on the face it would hit.
            hasMark = stopped;
            if (stopped)
            {
                var normal = new Vector3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z);
                if (normal == Vector3.zero) normal = Vector3.up;
                Vector3 spot = landing.ToUnity();
                float size = Mathf.Max(MarkMinSize, Vector3.Distance(spot, origin) * MarkPerBlock);
                markMatrix = Matrix4x4.TRS(spot + normal * 0.03f, Quaternion.FromToRotation(Vector3.up, normal), Vector3.one * size);
            }
        }

        public void Hide()
        {
            line.enabled = false;
            hasMark = false;
        }

        // Draws the mark (the line draws itself); to call every frame.
        public void Render()
        {
            if (!hasMark) return;

            var parameters = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            Graphics.RenderMesh(parameters, mark, 0, markMatrix);
        }

        public void Destroy()
        {
            if (line != null) Object.Destroy(line.gameObject);
            Object.Destroy(material);
            Object.Destroy(mark);
        }
    }
}
