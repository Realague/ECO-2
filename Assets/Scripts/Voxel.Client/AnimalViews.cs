using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Animals near the local player, as the server describes them, and their display. From time to
    // time the server sends the full list of nearby animals: when an animal has moved, it travels
    // from its old position to the new one at its species' own pace (hopping or walking), then waits
    // there. A move never lasts longer than one simulation step, so at high speeds it is quickened.
    //
    // The server only sends where each animal stands. The way there is worked out again on this
    // machine's world with the simulation's own rules (AnimalMoves), column by column, so the
    // animal follows the ground, climbs and drops at the block edges, and goes around walls.
    internal sealed class AnimalViews
    {
        private const int MaxInstancesPerCall = 1023;

        // Longest way worked out between two positions; beyond, or if the way is blocked, the rest is a straight line.
        private const int MaxRouteSteps = 12;

        private readonly AnimalSpeciesAsset[] assets;
        private readonly IReadOnlyList<AnimalSpecies> species;
        private readonly Dictionary<int, View> views = new Dictionary<int, View>();
        private readonly List<int> gone = new List<int>();
        // By species then by part: the matrices of the displayed animals, recomputed every frame.
        private readonly Matrix4x4[][][] matrices;
        private readonly int[] counts;
        private readonly RenderParams[][] renderParams;
        private int stamp;

        // Animals are not drawn without a display: assets is then null.
        public AnimalViews(AnimalSpeciesAsset[] assets, IReadOnlyList<AnimalSpecies> species, bool rendering)
        {
            this.species = species;
            if (!rendering) return;

            this.assets = assets;
            var everywhere = new Bounds(Vector3.zero, Vector3.one * 100000f);
            matrices = new Matrix4x4[assets.Length][][];
            counts = new int[assets.Length];
            renderParams = new RenderParams[assets.Length][];
            for (int s = 0; s < assets.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                matrices[s] = new Matrix4x4[parts.Count][];
                renderParams[s] = new RenderParams[parts.Count];
                for (int p = 0; p < parts.Count; p++)
                {
                    matrices[s][p] = new Matrix4x4[0];
                    renderParams[s][p] = new RenderParams(parts[p].material)
                    {
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true,
                        worldBounds = everywhere
                    };
                }
            }
        }

        public int Count => views.Count;

        // Start of a new list: animals that do not appear in it will be removed by EndList.
        public void BeginList()
        {
            stamp++;
        }

        // position: where the animal now stands (centre of its column, on the ground). world: this
        // machine's world, or null if it has none yet. maxTravelSeconds: the longest the move may take.
        public void Add(int animalId, int speciesIndex, Vector3 position, World world, float maxTravelSeconds)
        {
            if (speciesIndex >= species.Count) return;

            float now = Time.time;
            if (views.TryGetValue(animalId, out View view))
            {
                if (position != view.To) view.SetTrip(Route(world, view.PositionAt(now), position), now, assets?[speciesIndex], maxTravelSeconds);
            }
            else
            {
                view = new View { SpeciesIndex = speciesIndex };
                view.SetTrip(new[] { position }, now, null, 0f);
            }

            view.Stamp = stamp;
            views[animalId] = view;
        }

        // The columns crossed from one position to another, as the simulation would walk them: the
        // start, then the centre of each column on the way, ending at the destination.
        private static Vector3[] Route(World world, Vector3 from, Vector3 to)
        {
            var points = new List<Vector3> { from };
            int x = Mathf.FloorToInt(from.x);
            int z = Mathf.FloorToInt(from.z);
            int targetX = Mathf.FloorToInt(to.x);
            int targetZ = Mathf.FloorToInt(to.z);

            if (world != null)
            {
                var ground = new WorldGround(world);
                int y = ground.TryGetGround(x, z, out int startY) ? startY : Mathf.RoundToInt(from.y);
                for (int step = 0; step < MaxRouteSteps; step++)
                {
                    if (!AnimalMoves.TryNextStep(ground, x, y, z, targetX, targetZ, out x, out y, out z)) break;

                    points.Add(new Vector3(x + 0.5f, y, z + 0.5f));
                }
            }

            if (points[points.Count - 1] != to) points.Add(to);
            return points.ToArray();
        }

        public void EndList()
        {
            gone.Clear();
            foreach (KeyValuePair<int, View> entry in views)
            {
                if (entry.Value.Stamp != stamp) gone.Add(entry.Key);
            }

            foreach (int animalId in gone)
            {
                views.Remove(animalId);
            }
        }

        public void Remove(int animalId)
        {
            views.Remove(animalId);
        }

        public void Clear()
        {
            views.Clear();
        }

        // Where each animal is now, with its species index.
        public void GetPositions(List<KeyValuePair<int, Vector3>> into)
        {
            into.Clear();
            float now = Time.time;
            foreach (View view in views.Values)
            {
                into.Add(new KeyValuePair<int, Vector3>(view.SpeciesIndex, view.PositionAt(now)));
            }
        }

        // Finds the nearest animal along the ray, at most maxDistance away. The direction must be normalized.
        public bool TryPick(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out int animalId, out float distance)
        {
            animalId = 0;
            distance = maxDistance;
            bool found = false;

            foreach (KeyValuePair<int, View> entry in views)
            {
                AnimalSpecies kind = species[entry.Value.SpeciesIndex];
                Vector3 position = entry.Value.PositionAt(Time.time);
                if (entry.Value.TryHopPhase(Time.time, out float hop)) position.y += HopLift(entry.Value.SpeciesIndex, hop);
                float half = kind.Width * 0.5f;
                var min = new System.Numerics.Vector3(position.x - half, position.y, position.z - half);
                var max = new System.Numerics.Vector3(position.x + half, position.y + kind.Height, position.z + half);

                float hit = PlantShape.RayDistance(origin, direction, min, max);
                if (hit >= 0f && hit <= distance)
                {
                    distance = hit;
                    animalId = entry.Key;
                    found = true;
                }
            }

            return found;
        }

        // To call every frame.
        public void Render()
        {
            if (assets == null || views.Count == 0) return;

            for (int s = 0; s < counts.Length; s++)
            {
                counts[s] = 0;
            }

            foreach (View view in views.Values)
            {
                counts[view.SpeciesIndex]++;
            }

            for (int s = 0; s < counts.Length; s++)
            {
                for (int p = 0; p < matrices[s].Length; p++)
                {
                    if (matrices[s][p].Length < counts[s]) matrices[s][p] = new Matrix4x4[Mathf.NextPowerOfTwo(counts[s])];
                }

                counts[s] = 0;
            }

            float now = Time.time;
            foreach (View view in views.Values)
            {
                int s = view.SpeciesIndex;
                Vector3 position = view.PositionAt(now);
                Quaternion rotation = Quaternion.Euler(0f, view.FacingAt(now), 0f);
                AnimalSpeciesAsset asset = assets[s];
                IReadOnlyList<SpeciesAsset.Part> parts = asset.Parts;
                if (asset.MoveGait == AnimalSpeciesAsset.Gait.Hop && view.TryHopPhase(now, out float hop))
                {
                    WriteHopMatrices(asset, parts, s, counts[s], position, rotation, hop);
                }
                else if (asset.MoveGait == AnimalSpeciesAsset.Gait.Trot)
                {
                    bool moving = view.TryGetDistance(now, out float travelled);
                    WriteTrotMatrices(asset, parts, s, counts[s], position, rotation, moving, travelled, now);
                }
                else
                {
                    for (int p = 0; p < parts.Count; p++)
                    {
                        matrices[s][p][counts[s]] = Matrix4x4.TRS(position + rotation * parts[p].offset, rotation, parts[p].scale);
                    }
                }

                counts[s]++;
            }

            for (int s = 0; s < assets.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    for (int start = 0; start < counts[s]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, counts[s] - start);
                        Graphics.RenderMeshInstanced(renderParams[s][p], parts[p].mesh, 0, matrices[s][p], count, start);
                    }
                }
            }
        }

        // One hop: the body rises and tilts (nose up on take-off, down on landing), the hind legs
        // push back, the front legs reach forward, the ears fold back and the tail flicks up.
        // hop goes from 0 (take-off) to 1 (landing).
        private void WriteHopMatrices(AnimalSpeciesAsset asset, IReadOnlyList<SpeciesAsset.Part> parts, int s, int index, Vector3 position, Quaternion rotation, float hop)
        {
            float arc = Mathf.Sin(hop * Mathf.PI);
            float pitch = -15f * Mathf.Sin(hop * 2f * Mathf.PI);
            Vector3 bodyPivot = asset.BodyPivot;
            Matrix4x4 body = Matrix4x4.TRS(position + Vector3.up * (asset.HopHeight * arc), rotation, Vector3.one)
                * Matrix4x4.Translate(bodyPivot) * Matrix4x4.Rotate(Quaternion.Euler(pitch, 0f, 0f)) * Matrix4x4.Translate(-bodyPivot);

            for (int p = 0; p < parts.Count; p++)
            {
                AnimalSpeciesAsset.PartRig rig = asset.RigOf(p);
                Matrix4x4 local = Matrix4x4.TRS(parts[p].offset, Quaternion.identity, parts[p].scale);
                float angle = PartAngle(rig.motion) * arc;
                if (angle != 0f)
                {
                    local = Matrix4x4.Translate(rig.pivot) * Matrix4x4.Rotate(Quaternion.Euler(angle, 0f, 0f)) * Matrix4x4.Translate(-rig.pivot) * local;
                }

                matrices[s][p][index] = body * local;
            }
        }

        // A trot: the legs swing in diagonal pairs (front right with hind left), one full cycle per
        // stride, the body bobs twice per cycle and the tail sways. Standing still, the legs are
        // straight and only the tail sways, slowly. A part's side comes from its pivot: x < 0 is left.
        private void WriteTrotMatrices(AnimalSpeciesAsset asset, IReadOnlyList<SpeciesAsset.Part> parts, int s, int index,
            Vector3 position, Quaternion rotation, bool moving, float travelled, float time)
        {
            const float legSwing = 32f;
            const float bob = 0.02f;
            float phase = travelled / asset.StrideLength * 2f * Mathf.PI;
            float swing = moving ? Mathf.Sin(phase) : 0f;
            float tailSway = moving ? 12f * Mathf.Sin(phase) : 8f * Mathf.Sin(time * 1.7f);
            float lift = moving ? bob * Mathf.Abs(Mathf.Cos(phase)) : 0f;
            Matrix4x4 body = Matrix4x4.TRS(position + Vector3.up * lift, rotation, Vector3.one);

            for (int p = 0; p < parts.Count; p++)
            {
                AnimalSpeciesAsset.PartRig rig = asset.RigOf(p);
                Matrix4x4 local = Matrix4x4.TRS(parts[p].offset, Quaternion.identity, parts[p].scale);
                float side = rig.pivot.x < 0f ? -1f : 1f;
                Quaternion turn;
                switch (rig.motion)
                {
                    case AnimalSpeciesAsset.PartMotion.FrontLeg:
                        turn = Quaternion.Euler(legSwing * swing * side, 0f, 0f);
                        break;
                    case AnimalSpeciesAsset.PartMotion.HindLeg:
                        turn = Quaternion.Euler(-legSwing * swing * side, 0f, 0f);
                        break;
                    case AnimalSpeciesAsset.PartMotion.Tail:
                        turn = Quaternion.Euler(0f, tailSway, 0f);
                        break;
                    default:
                        turn = Quaternion.identity;
                        break;
                }

                if (turn != Quaternion.identity)
                {
                    local = Matrix4x4.Translate(rig.pivot) * Matrix4x4.Rotate(turn) * Matrix4x4.Translate(-rig.pivot) * local;
                }

                matrices[s][p][index] = body * local;
            }
        }

        // How high above the ground a hopping animal is at this point of its hop; zero for one that walks.
        private float HopLift(int speciesIndex, float hop)
        {
            if (assets == null || assets[speciesIndex].MoveGait != AnimalSpeciesAsset.Gait.Hop) return 0f;

            return assets[speciesIndex].HopHeight * Mathf.Sin(hop * Mathf.PI);
        }

        // Largest tilt of each kind of part during a hop, in degrees around the sideways axis.
        // Positive swings the lower end backwards.
        private static float PartAngle(AnimalSpeciesAsset.PartMotion motion)
        {
            switch (motion)
            {
                case AnimalSpeciesAsset.PartMotion.HindLeg: return 55f;
                case AnimalSpeciesAsset.PartMotion.FrontLeg: return -40f;
                case AnimalSpeciesAsset.PartMotion.Ear: return -30f;
                case AnimalSpeciesAsset.PartMotion.Tail: return 30f;
                default: return 0f;
            }
        }

        // The blocks as the display sees them: only the ground counts, other animals never block the way.
        private readonly struct WorldGround : IAnimalGround
        {
            private readonly World world;

            public WorldGround(World world)
            {
                this.world = world;
            }

            public bool TryGetGround(int x, int z, out int y)
            {
                y = 0;
                if (!world.InBounds(x, 0, z)) return false;

                y = world.GetSurfaceHeight(x, z);
                return y > 0 && y < world.SizeY;
            }

            public bool IsFree(int x, int z) => true;
        }

        // An animal on screen, and its current trip: a broken line through the columns it crosses.
        private struct View
        {
            public int SpeciesIndex;
            public int Stamp;

            // The trip: its points, the time each point is reached (from Start), the hops between
            // two points, and the direction faced on each leg.
            private Vector3[] points;
            private float[] arrivals;
            private float[] distances;
            private int[] hops;
            private float[] yaws;
            private float start;
            private float restingYaw;

            public Vector3 To => points[points.Length - 1];

            // Each leg takes the species' own time; the whole trip is quickened to fit in maxSeconds.
            public void SetTrip(Vector3[] route, float now, AnimalSpeciesAsset asset, float maxSeconds)
            {
                int legs = route.Length - 1;
                points = route;
                arrivals = new float[route.Length];
                distances = new float[route.Length];
                hops = new int[Mathf.Max(0, legs)];
                yaws = new float[Mathf.Max(0, legs)];
                start = now;

                for (int leg = 0; leg < legs; leg++)
                {
                    Vector3 travel = route[leg + 1] - route[leg];
                    travel.y = 0f;
                    float length = travel.magnitude;
                    distances[leg + 1] = distances[leg] + length;
                    hops[leg] = asset != null ? AnimalSpeciesAsset.HopCount(length, asset.HopLength) : 1;
                    arrivals[leg + 1] = arrivals[leg] + (asset != null ? asset.TravelSeconds(length) : 1f);
                    yaws[leg] = length > 0.001f ? Quaternion.LookRotation(travel).eulerAngles.y : (leg > 0 ? yaws[leg - 1] : restingYaw);
                }

                float total = arrivals[route.Length - 1];
                if (total > maxSeconds && total > 0f)
                {
                    for (int i = 0; i < arrivals.Length; i++)
                    {
                        arrivals[i] *= maxSeconds / total;
                    }
                }

                if (legs > 0) restingYaw = yaws[legs - 1];
            }

            public Vector3 PositionAt(float time)
            {
                if (!TryGetLeg(time, out int leg, out float progress)) return To;

                Vector3 from = points[leg];
                Vector3 to = points[leg + 1];
                Vector3 position = Vector3.Lerp(from, to, progress);
                position.y = HeightAlong(from.y, to.y, progress);
                return position;
            }

            public float FacingAt(float time) => TryGetLeg(time, out int leg, out _) ? yaws[leg] : restingYaw;

            // While travelling, where it is in its current hop: 0 at take-off, 1 at landing. Each leg
            // has a whole number of hops, so the animal lands in each column it crosses.
            public bool TryHopPhase(float time, out float phase)
            {
                phase = 0f;
                if (!TryGetLeg(time, out int leg, out float progress)) return false;

                float done = progress * hops[leg];
                phase = done - Mathf.Floor(done);
                return true;
            }

            // While travelling, the horizontal distance covered since the start of the trip.
            public bool TryGetDistance(float time, out float distance)
            {
                distance = 0f;
                if (!TryGetLeg(time, out int leg, out float progress)) return false;

                distance = Mathf.Lerp(distances[leg], distances[leg + 1], progress);
                return true;
            }

            // The leg being travelled at this time and how far along it, or false once arrived.
            private bool TryGetLeg(float time, out int leg, out float progress)
            {
                leg = 0;
                progress = 1f;
                if (points == null || points.Length < 2) return false;

                float elapsed = time - start;
                for (int i = 0; i < points.Length - 1; i++)
                {
                    if (elapsed >= arrivals[i + 1]) continue;

                    float length = arrivals[i + 1] - arrivals[i];
                    leg = i;
                    progress = length > 0f ? Mathf.Clamp01((elapsed - arrivals[i]) / length) : 1f;
                    return true;
                }

                return false;
            }

            // A step up is climbed before the edge between the two columns, in the first half of the
            // leg; a step down is taken after it. The body never cuts into the block it climbs onto.
            private static float HeightAlong(float from, float to, float progress)
            {
                float shape = to > from ? Mathf.SmoothStep(0f, 1f, progress * 2f) : Mathf.SmoothStep(0f, 1f, progress * 2f - 1f);
                return Mathf.Lerp(from, to, shape);
            }
        }
    }
}
