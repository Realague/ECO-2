using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Animals near the local player, as the server describes them, and their display. From time to
    // time the server sends the full list of nearby animals: an animal travels from where it is
    // shown to its new position over about the time until the next list (EcosystemSync). With time
    // to spare (speed 1) it wanders around its destination on the way (Route), at an easy pace, so
    // it keeps moving; hoppers pause briefly between hops and birds perch after their flight. At
    // high speeds the move is quickened and the animal moves on without stopping. Corners are
    // rounded, the animal turns to face its way, and a trip carries on from what the one before
    // had reached.
    //
    // The server only sends where each animal stands. The way there is worked out again on this
    // machine's world with the simulation's own rules (AnimalMoves), column by column, so the
    // animal follows the ground, climbs and drops at the block edges, and goes around walls.
    internal sealed class AnimalViews
    {
        private const int MaxInstancesPerCall = 1023;

        // Longest way worked out between two positions; beyond, or if the way is blocked, the rest is a straight line.
        private const int MaxRouteSteps = 12;

        // Corners of a route are rounded over this distance on each side, in blocks (under half a column).
        private const float CornerRadius = 0.35f;

        // How fast an animal turns to face a new way when a trip starts, and the longest it takes.
        private const float TurnDegreesPerSecond = 360f;
        private const float MaxTurnSeconds = 0.5f;

        // A hop cut short by the next trip comes down in this time.
        private const float LiftFadeSeconds = 0.15f;

        // Fastest a bird rises or comes down, in blocks per second (about, along its arc).
        private const float FlightClimbRate = 4f;

        // A move may last this much longer than the time until the next list, so that an animal
        // going on moving never stops just before it: the next move starts from where it is.
        private const float TravelMargin = 1.25f;

        // When there is time to spare (at speed 1 a step lasts 10 s and an animal moves 2 or 3
        // columns), a move fills the time until the next list: a walker slows down, to this share of
        // its speed at the slowest, with shorter strides, and stops now and then; a hopper pauses
        // between hops; birds fly at their pace and perch. Ending sooner would stop every animal
        // at once until the next list.
        private const float SlowestPace = 0.15f;

        // At speed 1 an animal moves 2 or 3 columns a step (10 s), far less than its pace: it is shown
        // wandering around its destination on the way, at this share of its pace (walkers and
        // hoppers, which then pause briefly between hops), or flying for this share of the time
        // (birds, which then perch), so that it keeps moving. The detours go around the
        // destination, a third of a turn each, at most MaxDetours of them, none shorter than MinDetour.
        private const float WanderPace = 0.4f;
        private const float FlightShare = 0.4f;
        private const int MaxWanderRadius = 4;
        private const int MaxDetours = 8;
        private const float MinDetour = 1f;

        // Legs swing out and settle back over this time when an animal sets off or stops.
        private const float StrideEaseSeconds = 0.25f;

        private readonly AnimalSpeciesAsset[] assets;
        private readonly IReadOnlyList<AnimalSpecies> species;
        private readonly Dictionary<int, View> views = new Dictionary<int, View>();
        private readonly List<int> gone = new List<int>();
        // By species then by part: the matrices of the displayed animals, recomputed every frame.
        private readonly Matrix4x4[][][] matrices;
        private readonly int[] counts;
        private readonly RenderParams[][] renderParams;

        // For the parts with a shadow mesh: drawing it into shadows only.
        private readonly RenderParams[][] shadowParams;
        private int stamp;

        // How deep an animal afloat sits: the water's surface is drawn below the top of its block.
        private const float FloatingDraft = 0.25f;

        // This machine's world, as last given to Add; null without one.
        private World world;

        // Carcasses near the local player: drawn as their species lying on its side, still.
        private readonly List<CarcassView> carcasses = new List<CarcassView>();

        // For each species, the height of its body's centre and half the body's width: a carcass
        // is turned over about that centre and laid on the ground. The body is the largest part.
        private readonly float[] bodyHeights;
        private readonly float[] bodyHalfWidths;

        // For each species, how far its parts reach from where it stands, with room for limbs that
        // swing, and the box around its animals this frame: Unity culls each batch by it (for the
        // camera and for each shadow cascade), so a species out of sight costs nothing to draw.
        private readonly float[] reaches;
        private readonly Vector3[] lowest;
        private readonly Vector3[] highest;

        private struct CarcassView
        {
            public int SpeciesIndex;
            public Vector3 Position;
            public Quaternion Facing;
        }

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
            shadowParams = new RenderParams[assets.Length][];
            bodyHeights = new float[assets.Length];
            bodyHalfWidths = new float[assets.Length];
            reaches = new float[assets.Length];
            lowest = new Vector3[assets.Length];
            highest = new Vector3[assets.Length];
            for (int s = 0; s < assets.Length; s++)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                float largest = 0f;
                float reach = 0f;
                foreach (SpeciesAsset.Part part in parts)
                {
                    // Boxes are unit cubes scaled; a modelled part is a mesh of its own size.
                    Bounds bounds = part.mesh.bounds;
                    Vector3 size = Vector3.Scale(bounds.size, part.scale);
                    Vector3 centre = part.offset + Vector3.Scale(bounds.center, part.scale);
                    reach = Mathf.Max(reach, centre.magnitude + 0.5f * new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)).magnitude);
                    float volume = size.x * size.y * size.z;
                    if (volume <= largest) continue;

                    largest = volume;
                    bodyHeights[s] = part.offset.y + bounds.center.y * part.scale.y;
                    bodyHalfWidths[s] = size.x * 0.5f;
                }

                // Turned any way, a part stays within reach of the animal's position; swinging limbs
                // and wings, a hop or a carcass rolled over stay within half as much again.
                reaches[s] = reach * 1.5f + 1f;
                matrices[s] = new Matrix4x4[parts.Count][];
                renderParams[s] = new RenderParams[parts.Count];
                shadowParams[s] = new RenderParams[parts.Count];
                for (int p = 0; p < parts.Count; p++)
                {
                    bool ownShadow = parts[p].shadowMesh != null;
                    matrices[s][p] = new Matrix4x4[0];
                    renderParams[s][p] = new RenderParams(parts[p].material)
                    {
                        shadowCastingMode = ownShadow ? ShadowCastingMode.Off : ShadowCastingMode.On,
                        receiveShadows = true,
                        worldBounds = everywhere
                    };
                    shadowParams[s][p] = new RenderParams(parts[p].material)
                    {
                        shadowCastingMode = ShadowCastingMode.ShadowsOnly,
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
        // machine's world, or null if it has none yet. listSeconds: about how long until the next list.
        public void Add(int animalId, int speciesIndex, Vector3 position, World world, float listSeconds)
        {
            if (speciesIndex >= species.Count) return;

            this.world = world;
            float now = Time.time;
            if (views.TryGetValue(animalId, out View view))
            {
                // A new trip when the animal has moved, or when it stayed put and its trip is nearly
                // over, so that it wanders on rather than standing still until the next list.
                bool moved = position != view.To;
                AnimalSpeciesAsset asset = assets?[speciesIndex];
                float wander = asset != null && world != null ? WanderLength(asset, listSeconds) : 0f;
                if (moved || (wander > 0f && view.RemainingSeconds(now) < listSeconds * 0.5f))
                {
                    Vector3[] route = Route(world, species[speciesIndex], view.PositionAt(now), position, wander, animalId + stamp * 7919);
                    view.SetTrip(route, now, asset, listSeconds, animalId);
                }
            }
            else
            {
                view = new View { SpeciesIndex = speciesIndex };
                view.SetTrip(new[] { position }, now, null, 0f, animalId);
            }

            view.Stamp = stamp;
            views[animalId] = view;
        }

        // How far an animal is shown going until the next list: WanderPace of its pace, or for a
        // bird FlightShare of the time at its pace (then it perches).
        private static float WanderLength(AnimalSpeciesAsset asset, float listSeconds)
        {
            switch (asset.MoveGait)
            {
                case AnimalSpeciesAsset.Gait.Hop: return WanderPace * asset.HopLength / asset.HopDuration * listSeconds;
                case AnimalSpeciesAsset.Gait.Fly: return FlightShare * asset.WalkSpeed * listSeconds;
                default: return WanderPace * asset.WalkSpeed * listSeconds;
            }
        }

        // The columns crossed from one position to another, as the simulation would walk them: the
        // start, then the centre of each column on the way, ending at the destination. When the
        // destination is nearer than `length`, the way first wanders by columns around it (picked from
        // seed, which changes with each list, so the loops differ), up to the species' move
        // range from it (so the server's reach check for a strike still holds) and at most
        // MaxWanderRadius, until its length is about `length`.
        private static Vector3[] Route(World world, AnimalSpecies kind, Vector3 from, Vector3 to, float length, int seed)
        {
            var points = new List<Vector3> { from };
            int x = Mathf.FloorToInt(from.x);
            int z = Mathf.FloorToInt(from.z);
            int targetX = Mathf.FloorToInt(to.x);
            int targetZ = Mathf.FloorToInt(to.z);

            if (world != null)
            {
                var ground = new WorldGround(world, kind.Habitat);
                bool flies = kind.Habitat == AnimalHabitat.Air;
                int y = ground.TryGetGround(x, z, out int startY) ? startY : Mathf.RoundToInt(from.y);
                float walked = 0f;
                int radius = Mathf.Clamp(kind.MoveRange, 1, MaxWanderRadius);
                float angle = Hash01(seed, targetX, targetZ, 0) * 2f * Mathf.PI;
                for (int detour = 1; detour <= MaxDetours; detour++)
                {
                    float spare = length - walked - Flat(points[points.Count - 1], to);
                    if (spare < MinDetour) break;

                    // Each detour goes on about a third of a turn around the destination, so the way loops.
                    angle += (0.55f + 0.3f * Hash01(seed, targetX, targetZ, detour)) * 2f * Mathf.PI / 3f;
                    float reach = Mathf.Min(radius, Mathf.Max(1f, spare * 0.5f));
                    int wayX = targetX + Mathf.RoundToInt(Mathf.Cos(angle) * reach);
                    int wayZ = targetZ + Mathf.RoundToInt(Mathf.Sin(angle) * reach);
                    Walk(ground, flies, ref x, ref y, ref z, wayX, wayZ, points, ref walked);
                }

                Walk(ground, flies, ref x, ref y, ref z, targetX, targetZ, points, ref walked);
            }

            if (points[points.Count - 1] != to) points.Add(to);
            return points.ToArray();
        }

        // Adds the columns walked toward a column, as far as the way goes (at most MaxRouteSteps).
        private static void Walk(WorldGround ground, bool flies, ref int x, ref int y, ref int z, int targetX, int targetZ,
            List<Vector3> points, ref float walked)
        {
            for (int step = 0; step < MaxRouteSteps; step++)
            {
                if (!AnimalMoves.TryNextStep(ground, x, y, z, targetX, targetZ, out x, out y, out z, flies)) return;

                var point = new Vector3(x + 0.5f, y, z + 0.5f);
                walked += Flat(points[points.Count - 1], point);
                points.Add(point);
            }
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // From 0 to 1, from whole numbers, the same on every machine.
        private static float Hash01(int a, int b, int c, int d)
        {
            int hash = (a * 73856093) ^ (b * 19349663) ^ (c * 83492791) ^ (d * 1640531527);
            hash ^= hash >> 13;
            hash *= 1274126177;
            return ((hash >> 8) & 0xFF) / 255f;
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
            carcasses.Clear();
        }

        // The carcasses near the local player, replacing the previous list. Each lies facing its own
        // way, picked from its column, the same on every machine.
        public void BeginCarcasses()
        {
            carcasses.Clear();
        }

        public void AddCarcass(int speciesIndex, Vector3 position)
        {
            if (speciesIndex >= species.Count) return;

            int yaw = (Mathf.FloorToInt(position.x) * 73 + Mathf.FloorToInt(position.z) * 151) % 360;
            carcasses.Add(new CarcassView { SpeciesIndex = speciesIndex, Position = position, Facing = Quaternion.Euler(0f, yaw, 0f) });
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
        // The species index of a displayed animal, or -1 if it is not displayed.
        public int SpeciesOf(int animalId) => views.TryGetValue(animalId, out View view) ? view.SpeciesIndex : -1;

        public bool TryPick(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float maxDistance, out int animalId, out float distance)
        {
            animalId = 0;
            distance = maxDistance;
            bool found = false;

            foreach (KeyValuePair<int, View> entry in views)
            {
                AnimalSpecies kind = species[entry.Value.SpeciesIndex];
                Vector3 position = entry.Value.PositionAt(Time.time);
                if (assets != null) position.y += entry.Value.LiftAt(Time.time, assets[entry.Value.SpeciesIndex]);
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
            if (assets == null || (views.Count == 0 && carcasses.Count == 0)) return;

            for (int s = 0; s < counts.Length; s++)
            {
                counts[s] = 0;
            }

            foreach (View view in views.Values)
            {
                counts[view.SpeciesIndex]++;
            }

            foreach (CarcassView carcass in carcasses)
            {
                counts[carcass.SpeciesIndex]++;
            }

            for (int s = 0; s < counts.Length; s++)
            {
                for (int p = 0; p < matrices[s].Length; p++)
                {
                    if (matrices[s][p].Length < counts[s]) matrices[s][p] = new Matrix4x4[Mathf.NextPowerOfTwo(counts[s])];
                }

                counts[s] = 0;
                lowest[s] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                highest[s] = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            }

            float now = Time.time;
            foreach (View view in views.Values)
            {
                int s = view.SpeciesIndex;
                Vector3 position = Afloat(s, view.PositionAt(now));
                Quaternion rotation = Quaternion.Euler(0f, view.FacingAt(now), 0f);
                AnimalSpeciesAsset asset = assets[s];
                IReadOnlyList<SpeciesAsset.Part> parts = asset.Parts;
                if (asset.MoveGait == AnimalSpeciesAsset.Gait.Hop && view.TryHopPhase(now, out float hop))
                {
                    position.y += view.CarriedLift(now);
                    WriteHopMatrices(asset, parts, s, counts[s], position, rotation, hop);
                }
                else if (asset.MoveGait == AnimalSpeciesAsset.Gait.Trot)
                {
                    view.GetStride(now, out float cycles, out float swing);
                    WriteTrotMatrices(asset, parts, s, counts[s], position, rotation, cycles, swing, now);
                }
                else if (asset.MoveGait == AnimalSpeciesAsset.Gait.Fly)
                {
                    float lift = view.FlightLift(now, asset.FlightHeight);
                    bool flying = view.IsTravelling(now) || lift > 0.01f;
                    position.y += lift;
                    WriteFlyMatrices(asset, parts, s, counts[s], position, rotation, flying, now);
                }
                else
                {
                    for (int p = 0; p < parts.Count; p++)
                    {
                        matrices[s][p][counts[s]] = Matrix4x4.TRS(position + rotation * parts[p].offset, rotation, parts[p].scale);
                    }
                }

                Enclose(s, position);
                counts[s]++;
            }

            // A carcass: the animal rolled onto its side about its body's centre, resting on the ground.
            foreach (CarcassView carcass in carcasses)
            {
                int s = carcass.SpeciesIndex;
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                Matrix4x4 lying = Matrix4x4.TRS(carcass.Position + Vector3.up * bodyHalfWidths[s], carcass.Facing, Vector3.one)
                    * Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, 90f)) * Matrix4x4.Translate(new Vector3(0f, -bodyHeights[s], 0f));
                for (int p = 0; p < parts.Count; p++)
                {
                    matrices[s][p][counts[s]] = lying * Matrix4x4.TRS(parts[p].offset, Quaternion.identity, parts[p].scale);
                }

                Enclose(s, carcass.Position);
                counts[s]++;
            }

            for (int s = 0; s < assets.Length; s++)
            {
                if (counts[s] == 0) continue;

                var bounds = new Bounds();
                bounds.SetMinMax(lowest[s] - Vector3.one * reaches[s], highest[s] + Vector3.one * reaches[s]);
                IReadOnlyList<SpeciesAsset.Part> parts = assets[s].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    renderParams[s][p].worldBounds = bounds;
                    shadowParams[s][p].worldBounds = bounds;
                    for (int start = 0; start < counts[s]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, counts[s] - start);
                        Graphics.RenderMeshInstanced(renderParams[s][p], parts[p].mesh, 0, matrices[s][p], count, start);
                        if (parts[p].shadowMesh != null)
                        {
                            Graphics.RenderMeshInstanced(shadowParams[s][p], parts[p].shadowMesh, 0, matrices[s][p], count, start);
                        }
                    }
                }
            }
        }

        private void Enclose(int speciesIndex, Vector3 position)
        {
            lowest[speciesIndex] = Vector3.Min(lowest[speciesIndex], position);
            highest[speciesIndex] = Vector3.Max(highest[speciesIndex], position);
        }

        // A shore animal above water floats in it, a little below the top of the block under it.
        private Vector3 Afloat(int speciesIndex, Vector3 position)
        {
            if (world == null || species[speciesIndex].Habitat != AnimalHabitat.Shore) return position;

            int x = Mathf.FloorToInt(position.x);
            int y = Mathf.FloorToInt(position.y + 0.01f);
            int z = Mathf.FloorToInt(position.z);
            if (world.IsWater(x, y - 1, z)) position.y -= FloatingDraft;
            return position;
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
        // stride (cycles, from View.GetStride), the body bobs twice per cycle and the tail sways;
        // strength (0 standing, 1 at full pace) scales the swing. Standing still, the legs are
        // straight and only the tail sways, slowly. A part's side comes from its pivot: x < 0 is left.
        private void WriteTrotMatrices(AnimalSpeciesAsset asset, IReadOnlyList<SpeciesAsset.Part> parts, int s, int index,
            Vector3 position, Quaternion rotation, float cycles, float strength, float time)
        {
            const float legSwing = 32f;
            const float bob = 0.02f;
            float phase = cycles * 2f * Mathf.PI;
            float swing = strength * Mathf.Sin(phase);
            float tailSway = Mathf.Lerp(8f * Mathf.Sin(time * 1.7f), 12f * Mathf.Sin(phase), Mathf.Clamp01(strength * 2f));
            float lift = strength * bob * Mathf.Abs(Mathf.Cos(phase));
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

        // A bird. Its wings are plates hanging from a hinge on the body's sides: in flight they are
        // raised out sideways and beat about that position (the right one mirroring the left); at rest
        // they hang folded, shortened to the asset's WingFold. The tail sways a little either way.
        private void WriteFlyMatrices(AnimalSpeciesAsset asset, IReadOnlyList<SpeciesAsset.Part> parts, int s, int index,
            Vector3 position, Quaternion rotation, bool flying, float time)
        {
            const float wingRaise = 75f;
            const float wingSwing = 40f;
            float beat = Mathf.Sin(time * asset.WingbeatsPerSecond * 2f * Mathf.PI);
            float tailSway = 6f * Mathf.Sin(time * 1.3f);
            Matrix4x4 body = Matrix4x4.TRS(position, rotation, Vector3.one);

            for (int p = 0; p < parts.Count; p++)
            {
                AnimalSpeciesAsset.PartRig rig = asset.RigOf(p);
                Matrix4x4 local = Matrix4x4.TRS(parts[p].offset, Quaternion.identity, parts[p].scale);
                float side = rig.pivot.x < 0f ? -1f : 1f;
                Matrix4x4 move;
                switch (rig.motion)
                {
                    case AnimalSpeciesAsset.PartMotion.Wing:
                        move = flying
                            ? Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, (wingRaise + wingSwing * beat) * side))
                            : Matrix4x4.Scale(new Vector3(1f, asset.WingFold, 1f));
                        break;
                    case AnimalSpeciesAsset.PartMotion.Tail:
                        move = Matrix4x4.Rotate(Quaternion.Euler(0f, tailSway, 0f));
                        break;
                    default:
                        move = Matrix4x4.identity;
                        break;
                }

                if (move != Matrix4x4.identity)
                {
                    local = Matrix4x4.Translate(rig.pivot) * move * Matrix4x4.Translate(-rig.pivot) * local;
                }

                matrices[s][p][index] = body * local;
            }
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

        // The blocks as the display sees them, for one habitat: only the ground (or the water) counts,
        // other animals never block the way.
        private readonly struct WorldGround : IAnimalGround
        {
            private readonly World world;
            private readonly AnimalHabitat habitat;

            public WorldGround(World world, AnimalHabitat habitat)
            {
                this.world = world;
                this.habitat = habitat;
            }

            public bool TryGetGround(int x, int z, out int y)
            {
                y = 0;
                if (!world.InBounds(x, 0, z)) return false;

                return AnimalMoves.TryGetGround(world, habitat, x, z, out y) && y < world.SizeY;
            }

            public bool IsFree(int x, int z) => true;
        }

        // An animal on screen, and its current trip: a broken line through the columns it crosses
        // (a hopper's cut into one leg per hop), its corners rounded, each leg possibly after a
        // pause. What the trip under way had reached when a new one starts (facing, stride, height
        // in a hop or in flight) is carried on, so the animal never jumps.
        private struct View
        {
            public int SpeciesIndex;
            public int Stamp;

            // The trip: its points, the time each point is reached and each leg sets off (from
            // start: leg i waits at point i from arrivals[i] to departures[i]), the distance covered
            // at each point, the direction of each leg, and the radius each point's corner is
            // rounded to (none where the animal pauses). A run is a stretch of legs without pause:
            // the stride eases in and out at its ends.
            private Vector3[] points;
            private float[] arrivals;
            private float[] departures;
            private float[] distances;
            private float[] yaws;
            private Vector3[] directions;
            private float[] radii;
            private float[] runStarts;
            private float[] runEnds;
            private float start;
            private float restingYaw;

            // The length of a stride, shortened by strideScale when the animal walks slower than its pace.
            private float strideLength;
            private float strideScale;

            // Carried over from the trip before: the way it faced, where its stride was, and how high it was.
            private float startYaw;
            private float turnSeconds;
            private float startCycles;
            private float startLift;

            // Quickened to last until the next list (at high speed): the animal is likely to go on
            // moving, so a bird stays up at its end.
            private bool continuous;

            public Vector3 To => points[points.Length - 1];

            // Each leg takes the species' own time; the whole trip is quickened to fit in about the time
            // until the next list, or, with time to spare, slowed down and spread over it with pauses.
            // seed varies the pauses from one animal to the next, so that a herd does not move as one.
            public void SetTrip(Vector3[] route, float now, AnimalSpeciesAsset asset, float listSeconds, int seed)
            {
                startYaw = FacingAt(now);
                GetStride(now, out float cycles, out float swing);
                startCycles = cycles;
                bool wasStriding = swing > 0f;
                startLift = asset != null ? LiftAt(now, asset) : 0f;

                AnimalSpeciesAsset.Gait gait = asset != null ? asset.MoveGait : AnimalSpeciesAsset.Gait.Glide;
                if (gait == AnimalSpeciesAsset.Gait.Hop) route = SplitIntoHops(route, asset.HopLength);

                int legs = route.Length - 1;
                points = route;
                arrivals = new float[route.Length];
                departures = new float[Mathf.Max(0, legs)];
                distances = new float[route.Length];
                yaws = new float[Mathf.Max(0, legs)];
                directions = new Vector3[Mathf.Max(0, legs)];
                radii = new float[route.Length];
                runStarts = new float[Mathf.Max(0, legs)];
                runEnds = new float[Mathf.Max(0, legs)];
                start = now;
                strideScale = 1f;
                strideLength = asset != null ? asset.StrideLength : 1f;

                var moveSeconds = new float[Mathf.Max(0, legs)];
                float natural = 0f;
                for (int leg = 0; leg < legs; leg++)
                {
                    Vector3 travel = route[leg + 1] - route[leg];
                    travel.y = 0f;
                    float length = travel.magnitude;
                    distances[leg + 1] = distances[leg] + length;
                    moveSeconds[leg] = asset != null ? asset.TravelSeconds(length) : 1f;
                    natural += moveSeconds[leg];
                    directions[leg] = length > 0.001f ? travel / length : Vector3.zero;
                    yaws[leg] = length > 0.001f ? Quaternion.LookRotation(travel).eulerAngles.y : (leg > 0 ? yaws[leg - 1] : restingYaw);
                }

                // Quickened, at the species' pace, or slowed down with pauses.
                float maxSeconds = listSeconds * TravelMargin;
                continuous = natural > maxSeconds && natural > 0f;
                float timeScale = continuous ? maxSeconds / natural : 1f;
                var pauses = new float[Mathf.Max(0, legs)];
                if (!continuous && legs > 0 && asset != null && gait != AnimalSpeciesAsset.Gait.Fly)
                {
                    float fill = listSeconds;
                    if (gait != AnimalSpeciesAsset.Gait.Hop)
                    {
                        float pace = Mathf.Clamp(natural / fill, SlowestPace, 1f);
                        timeScale = 1f / pace;
                        strideScale = Mathf.Sqrt(pace);
                        strideLength *= strideScale;
                    }

                    SpreadPauses(pauses, fill - natural * timeScale, seed, route[legs]);
                }

                float time = 0f;
                for (int leg = 0; leg < legs; leg++)
                {
                    departures[leg] = time + pauses[leg];
                    time = departures[leg] + moveSeconds[leg] * timeScale;
                    arrivals[leg + 1] = time;
                }

                // A corner stays within the column of its point (CornerRadius < 0.5), so no wall is
                // cut; where the animal pauses, it stands on the point itself.
                for (int i = 1; i < legs; i++)
                {
                    radii[i] = pauses[i] > 0f ? 0f
                        : Mathf.Min(CornerRadius, 0.5f * (distances[i] - distances[i - 1]), 0.5f * (distances[i + 1] - distances[i]));
                }

                for (int leg = 0; leg < legs; leg++)
                {
                    bool setsOff = leg == 0 ? pauses[0] > 0f || !wasStriding : pauses[leg] > 0f;
                    runStarts[leg] = setsOff ? departures[leg] : leg == 0 ? -StrideEaseSeconds : runStarts[leg - 1];
                }

                for (int leg = legs - 1; leg >= 0; leg--)
                {
                    runEnds[leg] = leg == legs - 1 || pauses[leg + 1] > 0f ? arrivals[leg + 1] : runEnds[leg + 1];
                }

                if (legs > 0) restingYaw = yaws[legs - 1];
                turnSeconds = legs > 0 ? Mathf.Min(MaxTurnSeconds, Mathf.Abs(Mathf.DeltaAngle(startYaw, yaws[0])) / TurnDegreesPerSecond) : 0f;
            }

            // The idle time shared among the pauses before each leg, unevenly, the same way on every machine.
            private static void SpreadPauses(float[] pauses, float idle, int seed, Vector3 to)
            {
                if (idle <= 0f) return;

                float total = 0f;
                for (int i = 0; i < pauses.Length; i++)
                {
                    int hash = (seed * 73856093) ^ (Mathf.FloorToInt(to.x) * 19349663) ^ (Mathf.FloorToInt(to.z) * 83492791) ^ (i * 1640531527);
                    hash ^= hash >> 13;
                    hash *= 1274126177;
                    pauses[i] = 0.3f + 1.4f * (((hash >> 8) & 0xFF) / 255f);
                    total += pauses[i];
                }

                for (int i = 0; i < pauses.Length; i++)
                {
                    pauses[i] *= idle / total;
                }
            }

            // The same way, with a point between each hop, so that each hop is a leg of its own and the
            // animal can pause between hops. Heights change where they did along the leg cut up.
            private static Vector3[] SplitIntoHops(Vector3[] route, float hopLength)
            {
                var points = new List<Vector3> { route[0] };
                for (int leg = 0; leg < route.Length - 1; leg++)
                {
                    Vector3 from = route[leg];
                    Vector3 to = route[leg + 1];
                    Vector3 travel = to - from;
                    travel.y = 0f;
                    int hops = AnimalSpeciesAsset.HopCount(travel.magnitude, hopLength);
                    for (int hop = 1; hop < hops; hop++)
                    {
                        float share = (float)hop / hops;
                        Vector3 point = Vector3.Lerp(from, to, share);
                        point.y = HeightAlong(from.y, to.y, share);
                        points.Add(point);
                    }

                    points.Add(to);
                }

                return points.ToArray();
            }

            public bool IsTravelling(float time) => TryGetLeg(time, out _, out _, out _);

            // How long until the trip ends, 0 once arrived.
            public float RemainingSeconds(float time) => points == null || points.Length < 2 ? 0f : Mathf.Max(0f, start + arrivals[arrivals.Length - 1] - time);

            public Vector3 PositionAt(float time)
            {
                if (!TryGetLeg(time, out int leg, out float progress, out _)) return To;

                Vector3 from = points[leg];
                Vector3 to = points[leg + 1];
                Vector3 position = TryGetCorner(leg, progress, out int corner, out float u)
                    ? Bezier(points[corner] - directions[corner - 1] * radii[corner], points[corner], points[corner] + directions[corner] * radii[corner], u)
                    : Vector3.Lerp(from, to, progress);
                position.y = HeightAlong(from.y, to.y, progress);
                return position;
            }

            // The way the path goes, turned to at TurnDegreesPerSecond: from the way the animal faced
            // before when the trip starts, and at the end of a pause toward the next leg.
            public float FacingAt(float time)
            {
                float path = restingYaw;
                if (TryGetLeg(time, out int leg, out float progress, out bool moving))
                {
                    path = yaws[leg];
                    if (!moving && leg > 0)
                    {
                        float turn = Mathf.Abs(Mathf.DeltaAngle(yaws[leg - 1], yaws[leg])) / TurnDegreesPerSecond;
                        float turnStart = departures[leg] - turn;
                        float elapsed = time - start;
                        path = elapsed <= turnStart || turn <= 0f ? yaws[leg - 1]
                            : Mathf.LerpAngle(yaws[leg - 1], yaws[leg], Mathf.SmoothStep(0f, 1f, (elapsed - turnStart) / turn));
                    }
                    else if (moving && TryGetCorner(leg, progress, out int corner, out float u))
                    {
                        Vector3 heading = Vector3.Lerp(directions[corner - 1], directions[corner], u);
                        if (heading.sqrMagnitude > 1e-6f) path = Quaternion.LookRotation(heading).eulerAngles.y;
                    }
                }

                float since = time - start;
                if (since >= turnSeconds) return path;

                return Mathf.LerpAngle(startYaw, path, Mathf.SmoothStep(0f, 1f, since / turnSeconds));
            }

            // While hopping, where it is in its hop: 0 at take-off, 1 at landing (each hop is a leg).
            public bool TryHopPhase(float time, out float phase)
            {
                phase = 0f;
                if (!TryGetLeg(time, out _, out float progress, out bool moving) || !moving) return false;

                phase = progress;
                return true;
            }

            // The height of a hop the trip before was in the middle of, coming down quickly.
            public float CarriedLift(float time) => startLift * Mathf.Max(0f, 1f - (time - start) / LiftFadeSeconds);

            // How high above its ground the animal is drawn: in a hop, or in flight.
            public float LiftAt(float time, AnimalSpeciesAsset asset)
            {
                switch (asset.MoveGait)
                {
                    case AnimalSpeciesAsset.Gait.Hop:
                        return CarriedLift(time) + (TryHopPhase(time, out float hop) ? asset.HopHeight * Mathf.Sin(hop * Mathf.PI) : 0f);
                    case AnimalSpeciesAsset.Gait.Fly:
                        return FlightLift(time, asset.FlightHeight);
                    default:
                        return 0f;
                }
            }

            // A flight rises in an arc over the way, from the height it had, and comes down to perch
            // at its end, never faster than FlightClimbRate: a short flight stays low, and what is
            // left of the way down is flown once there. A continuous one climbs on from trip to trip,
            // stays up at its end and only comes down if no move follows.
            public float FlightLift(float time, float height)
            {
                if (points == null) return 0f;

                float elapsed = time - start;
                float total = arrivals[arrivals.Length - 1];
                float halfClimb = FlightClimbRate * total * 0.5f;
                float peak = Mathf.Min(height, Mathf.Max(startLift, continuous ? startLift + halfClimb : halfClimb));
                float end = continuous ? peak : Mathf.Max(0f, peak - halfClimb);
                if (elapsed < total)
                {
                    float progress = elapsed / total;
                    return Mathf.Lerp(progress < 0.5f ? startLift : end, peak, Mathf.Sin(progress * Mathf.PI));
                }

                if (end <= 0f) return 0f;

                float landing = 1.5f * end / FlightClimbRate;
                return end * (1f - Mathf.SmoothStep(0f, 1f, (elapsed - total) / landing));
            }

            // The stride: how many cycles the legs have swung, the trips before included, and how far
            // they swing (0 standing, up to 1 at full pace, shorter when walking slowly, easing in
            // and out when the animal sets off and stops).
            public void GetStride(float time, out float cycles, out float swing)
            {
                cycles = 0f;
                swing = 0f;
                if (!TryGetLeg(time, out int leg, out float progress, out bool moving) || !moving) return;

                float travelled = Mathf.Lerp(distances[leg], distances[leg + 1], progress);
                cycles = startCycles + travelled / strideLength;
                float elapsed = time - start;
                float ease = Mathf.Min(1f, (elapsed - runStarts[leg]) / StrideEaseSeconds, (runEnds[leg] - elapsed) / StrideEaseSeconds);
                swing = Mathf.Max(0f, ease) * strideScale;
            }

            // The leg under way at this time, how far along it, and whether the animal is moving on
            // it or still pausing at its start; false once arrived.
            private bool TryGetLeg(float time, out int leg, out float progress, out bool moving)
            {
                leg = 0;
                progress = 1f;
                moving = false;
                if (points == null || points.Length < 2) return false;

                float elapsed = time - start;
                for (int i = 0; i < points.Length - 1; i++)
                {
                    if (elapsed >= arrivals[i + 1]) continue;

                    leg = i;
                    if (elapsed < departures[i])
                    {
                        progress = 0f;
                        return true;
                    }

                    float length = arrivals[i + 1] - departures[i];
                    progress = length > 0f ? Mathf.Clamp01((elapsed - departures[i]) / length) : 1f;
                    moving = true;
                    return true;
                }

                return false;
            }

            // Whether this point of a leg lies on a rounded corner: the last radius of a leg and the
            // first of the next one, around the point between them, crossed from u = 0 to 1.
            private bool TryGetCorner(int leg, float progress, out int corner, out float u)
            {
                float length = distances[leg + 1] - distances[leg];
                float along = progress * length;
                corner = leg;
                u = 0f;
                if (leg > 0 && along < radii[leg])
                {
                    u = 0.5f + along / (2f * radii[leg]);
                    return true;
                }

                corner = leg + 1;
                float left = length - along;
                if (corner < points.Length - 1 && left < radii[corner])
                {
                    u = 0.5f - left / (2f * radii[corner]);
                    return true;
                }

                return false;
            }

            private static Vector3 Bezier(Vector3 a, Vector3 control, Vector3 b, float u)
            {
                float v = 1f - u;
                return v * v * a + 2f * u * v * control + u * u * b;
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
