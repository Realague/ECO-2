using System.Collections.Generic;
using Eco.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // What a machine knows about a plant to display it and aim at it.
    internal struct PlantView
    {
        public int SpeciesIndex;
        public int X;
        public int Y;
        public int Z;
        public int Stage;

        // Stage of ripeness of a crop, 0 (nothing to harvest) to Plant.RipeStageCount.
        public int RipeStage;

        // The stump of a felled tree (Plant.IsStump), drawn as its species' stump at the tree's size.
        public bool Stump;
    }

    // Draws plants in batches (GPU instancing), without creating one object per plant. Plants are
    // grouped by world region: only the regions near the camera are prepared and drawn,
    // which keeps the cost constant whatever the size of the world.
    //
    // A species may have several models; each plant shows the one its column picks
    // (SpeciesAsset.ModelAt), turned by a quarter turn also picked from its column. Batches are
    // made per look, that is per model of a species, then per part. A ripe crop also shows the
    // fruit of its model at its stage of ripeness, each stage a look of its own; a stump, its model's
    // stump. A felled tree falls (StartFall), drawn on its own for the few seconds it takes.
    internal sealed class PlantRenderer
    {
        // A felled tree takes this long to fall flat, then bounces, lies a moment and shrinks away.
        public const float FallSeconds = 1f;
        private const float BounceSeconds = 0.25f;
        private const float BounceDegrees = 4f;
        private const float LieSeconds = 0.2f;
        private const float ShrinkSeconds = 0.3f;
        private const float FallViewDistance = 160f;

        // A tree shown lying this recently is still waiting for its fall to be played.
        private const float RecentSeconds = 2f;

        private const float CutDiscShare = SpeciesAsset.CutDiscShare;

        // Once down, a felled tree slides off its stump, so that the stump shows: its foot lies this far
        // from the stump's middle, where its first log will be (EcosystemSync.FirstLogDistance less
        // half a log), its trunk resting on the ground.
        public const float LyingFootDistance = PlantShape.StumpHeight + 0.6f - LogViews.Length * 0.5f;

        // A pruned tree's trunk (SpeciesAsset.BareTrunkOf) is clipped to lengths rounded up to this.
        private const float BareStep = 0.05f;

        private const int RegionSize = 32;
        private const int MaxInstancesPerCall = 1023;

        // Margin around a region for foliage that sticks out.
        private const float RegionMargin = 4f;

        // Each plant is turned by a quarter turn chosen from its column, so that neighbours of the
        // same species do not look like copies. Display only: the same on every machine, never sent.
        private static readonly Quaternion[] Turns =
        {
            Quaternion.identity, Quaternion.Euler(0f, 90f, 0f), Quaternion.Euler(0f, 180f, 0f), Quaternion.Euler(0f, 270f, 0f)
        };

        private readonly SpeciesAsset[] species;

        // By species: the index of its first look; its models follow.
        private readonly int[] firstLook;

        // By model look, then by stage of ripeness (from 1): the look of its fruit, or -1 for none.
        private readonly int[][] fruitLooks;

        // By model look: the look of its stump, or -1 for none.
        private readonly int[] stumpLooks;

        // Trees falling now.
        private readonly List<Fall> falls = new List<Fall>();

        // By look: its parts and how to draw each.
        private readonly IReadOnlyList<SpeciesAsset.Part>[] lookParts;

        // By look: the lowest and highest point of its parts above the plant's foot, at full size.
        private readonly float[] lookBottoms;
        private readonly float[] lookTops;
        private readonly RenderParams[][] renderParams;
        private readonly Dictionary<long, Region> regions = new Dictionary<long, Region>();

        // By model look: its species and model, and its trunk's half width at full size, from its
        // stump's cut disc (0 without a stump).
        private readonly int[] lookSpecies;
        private readonly int[] lookModel;
        private readonly float[] trunkHalves;

        // By model look: its bare trunk (BareTrunkOf), made when first needed; and its meshes clipped to
        // a length (by look and length in BareStep).
        private readonly Dictionary<int, BareTrunk> bareTrunks = new Dictionary<int, BareTrunk>();
        private readonly Dictionary<long, Mesh[]> bareMeshes = new Dictionary<long, Mesh[]>();

        // The world shown and whether its ground is smoothed (ShowOn).
        private World ground;
        private bool smoothShown;
        private float strengthShown = float.NaN;

        public PlantRenderer(SpeciesAsset[] species)
        {
            this.species = species;
            firstLook = new int[species.Length];
            var parts = new List<IReadOnlyList<SpeciesAsset.Part>>();
            for (int s = 0; s < species.Length; s++)
            {
                firstLook[s] = parts.Count;
                for (int m = 0; m < species[s].ModelCount; m++)
                {
                    parts.Add(species[s].PartsOf(m));
                }
            }

            // Then the fruit of each model at each stage of ripeness, as looks of their own.
            fruitLooks = new int[parts.Count][];
            int model = 0;
            for (int s = 0; s < species.Length; s++)
            {
                for (int m = 0; m < species[s].ModelCount; m++, model++)
                {
                    fruitLooks[model] = new int[Plant.RipeStageCount];
                    for (int ripeStage = 1; ripeStage <= Plant.RipeStageCount; ripeStage++)
                    {
                        IReadOnlyList<SpeciesAsset.Part> fruit = species[s].FruitPartsOf(m, ripeStage);
                        fruitLooks[model][ripeStage - 1] = fruit.Count > 0 ? parts.Count : -1;
                        if (fruit.Count > 0) parts.Add(fruit);
                    }
                }
            }

            // Then the stump of each model, if it leaves one.
            stumpLooks = new int[fruitLooks.Length];
            model = 0;
            for (int s = 0; s < species.Length; s++)
            {
                for (int m = 0; m < species[s].ModelCount; m++, model++)
                {
                    IReadOnlyList<SpeciesAsset.Part> stump = species[s].StumpPartsOf(m);
                    stumpLooks[model] = stump.Count > 0 ? parts.Count : -1;
                    if (stump.Count > 0) parts.Add(stump);
                }
            }

            lookParts = parts.ToArray();

            // The species and model of each model look, and its trunk's half width, from its stump.
            lookSpecies = new int[fruitLooks.Length];
            lookModel = new int[fruitLooks.Length];
            trunkHalves = new float[fruitLooks.Length];
            model = 0;
            for (int s = 0; s < species.Length; s++)
            {
                for (int m = 0; m < species[s].ModelCount; m++, model++)
                {
                    lookSpecies[model] = s;
                    lookModel[model] = m;
                    if (stumpLooks[model] < 0) continue;

                    IReadOnlyList<SpeciesAsset.Part> stump = lookParts[stumpLooks[model]];
                    SpeciesAsset.Part cut = stump[stump.Count - 1];
                    Vector3 extents = Vector3.Scale(cut.mesh.bounds.extents, cut.scale) / SpeciesAsset.CutDiscShare;
                    trunkHalves[model] = Mathf.Max(Mathf.Abs(extents.x), Mathf.Abs(extents.z));
                }
            }

            lookBottoms = new float[lookParts.Length];
            lookTops = new float[lookParts.Length];
            renderParams = new RenderParams[lookParts.Length][];
            for (int look = 0; look < lookParts.Length; look++)
            {
                // Plants only turn about the vertical, so heights do not depend on the turn.
                float bottom = float.MaxValue;
                float top = float.MinValue;
                foreach (SpeciesAsset.Part part in lookParts[look])
                {
                    Bounds bounds = part.mesh.bounds;
                    float a = part.offset.y + bounds.min.y * part.scale.y;
                    float b = part.offset.y + bounds.max.y * part.scale.y;
                    bottom = Mathf.Min(bottom, Mathf.Min(a, b));
                    top = Mathf.Max(top, Mathf.Max(a, b));
                }

                lookBottoms[look] = lookParts[look].Count > 0 ? bottom : 0f;
                lookTops[look] = lookParts[look].Count > 0 ? top : 0f;

                renderParams[look] = new RenderParams[lookParts[look].Count];
                for (int p = 0; p < lookParts[look].Count; p++)
                {
                    renderParams[look][p] = new RenderParams(lookParts[look][p].material)
                    {
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true
                    };
                }
            }
        }

        // Adds a plant or updates its growth stage. A plant never changes place.
        public void Set(int plantId, PlantView plant)
        {
            long key = KeyOf(plant.X / RegionSize, plant.Z / RegionSize);
            if (!regions.TryGetValue(key, out Region region))
            {
                region = new Region(plant.X / RegionSize, plant.Z / RegionSize, lookParts);
                regions.Add(key, region);
            }

            region.Plants[plantId] = plant;
            region.Dirty = true;
        }

        public void Remove(int plantId, PlantView plant)
        {
            if (!regions.TryGetValue(KeyOf(plant.X / RegionSize, plant.Z / RegionSize), out Region region)) return;

            if (region.Plants.Remove(plantId)) region.Dirty = true;
        }

        public void Clear()
        {
            regions.Clear();
            falls.Clear();
        }

        // To call every frame before Render: the world the plants stand on and whether its ground is
        // drawn smoothed (TerrainSettings), for the height the ground shows under each plant
        // (SmoothTerrain.HeightAt). Every plant is placed again when either changes, and those near a
        // block that changes.
        public void ShowOn(World world, bool smoothTerrain)
        {
            if (world == ground && smoothTerrain == smoothShown && SmoothTerrain.Strength == strengthShown) return;

            strengthShown = SmoothTerrain.Strength;

            if (ground != null)
            {
                ground.BlockChanged -= OnBlockChanged;
                ground.Pins.Changed -= OnPinChanged;
            }

            ground = world;
            smoothShown = smoothTerrain;
            if (ground != null)
            {
                ground.BlockChanged += OnBlockChanged;
                ground.Pins.Changed += OnPinChanged;
            }
            foreach (Region region in regions.Values) region.Dirty = true;
        }

        // A block changed, or storage was placed or taken (World.Pins): the smoothed ground moves under
        // the plants of the columns around it.
        private void OnPinChanged(BlockPos cell) => OnBlockChanged(cell, 0, 0);

        private void OnBlockChanged(BlockPos pos, ushort previous, ushort current)
        {
            if (!smoothShown) return;

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = pos.X + dx, z = pos.Z + dz;
                    if (x < 0 || z < 0) continue;
                    if (regions.TryGetValue(KeyOf(x / RegionSize, z / RegionSize), out Region region)) region.Dirty = true;
                }
            }
        }

        // Where a plant stands: the middle of its column on its ground, on smoothed ground the height
        // the ground shows there; a floating plant at the water's surface.
        private Vector3 FootOf(PlantView plant)
        {
            var foot = new Vector3(plant.X + 0.5f, plant.Y, plant.Z + 0.5f);
            if (species[plant.SpeciesIndex].FloatsOnWater)
            {
                foot.y += ChunkMesher.WaterSurface;
            }
            else if (smoothShown && ground != null && !ground.IsWater(plant.X, plant.Y, plant.Z))
            {
                foot.y = SmoothTerrain.HeightAt(ground, plant.X, plant.Z, plant.Y, 0.5f, 0.5f);
            }

            return foot;
        }

        // A felled tree falls the way yaw points (radians about the vertical, 0 toward +z), about its
        // foot just above where its stump stays.
        public void StartFall(PlantView plant, float yaw)
        {
            plant.Stump = false;
            int look = LookOf(plant);
            if (look < 0) return;

            // Told to lie before its fall was played (the messages crossed): it falls now, then lies.
            for (int i = 0; i < falls.Count; i++)
            {
                Fall lying = falls[i];
                if (lying.LyingId == 0 || lying.X != plant.X || lying.Z != plant.Z || Time.time - lying.Shown > RecentSeconds) continue;

                Fall replayed = NewFall(look, plant, yaw);
                replayed.LyingId = lying.LyingId;
                falls[i] = replayed;
                return;
            }

            falls.Add(NewFall(look, plant, yaw));
        }

        // A felled tree stays lying where it fell, with its branches, until EndLying (FallenTrees,
        // LogSync): its fall just played is kept lying, else it is shown lying already.
        public void ShowLying(int lyingId, PlantView plant, float yaw)
        {
            plant.Stump = false;
            int look = LookOf(plant);
            if (look < 0) return;

            float now = Time.time;
            for (int i = 0; i < falls.Count; i++)
            {
                Fall fall = falls[i];
                if (fall.LyingId == lyingId) return;
                if (fall.LyingId != 0 || fall.X != plant.X || fall.Z != plant.Z || now - fall.Started > FallSeconds + BounceSeconds + LieSeconds) continue;

                fall.LyingId = lyingId;
                falls[i] = fall;
                return;
            }

            Fall lying = NewFall(look, plant, yaw);
            lying.LyingId = lyingId;
            lying.Started = now - FallSeconds - BounceSeconds;
            falls.Add(lying);
        }

        // A fallen tree pruned: from now on only its trunk shows, bare, from its foot to length along
        // the way it fell from its stump's middle (its logs still to buck), clipped there. False when
        // its model has no bare trunk (no stump): it is then left to shrink away, and its logs show.
        public bool SetLyingBare(int lyingId, float length)
        {
            for (int i = 0; i < falls.Count; i++)
            {
                Fall fall = falls[i];
                if (fall.LyingId != lyingId) continue;

                BareTrunk bare = BareTrunkOf(fall.Look);
                if (bare == null)
                {
                    EndLying(lyingId);
                    return false;
                }

                // Its foot lies at LyingFootDistance; what is past length is bucked off.
                float kept = Mathf.Max(0f, (length - LyingFootDistance) / fall.Size);
                fall.BareKey = ((long)fall.Look << 32) | (uint)Mathf.CeilToInt(kept / BareStep);
                falls[i] = fall;
                return true;
            }

            return false;
        }

        // A fallen tree pruned with no bare trunk, bucked or broken up: it shrinks away; or, at once,
        // gone (its last log off).
        public void EndLying(int lyingId, bool atOnce = false)
        {
            float now = Time.time;
            for (int i = falls.Count - 1; i >= 0; i--)
            {
                Fall fall = falls[i];
                if (fall.LyingId != lyingId) continue;

                if (atOnce || fall.BareKey >= 0)
                {
                    falls.RemoveAt(i);
                    continue;
                }

                fall.LyingId = 0;
                fall.Started = Mathf.Min(fall.Started, now - FallSeconds - BounceSeconds - LieSeconds);
                falls[i] = fall;
            }
        }

        // The bare trunk of a model look (SpeciesAsset.BareTrunkOf: its wood's boxes near the trunk's
        // axis, no branches, no leaves) with its meshes' data, read once; null without one.
        private BareTrunk BareTrunkOf(int look)
        {
            if (bareTrunks.TryGetValue(look, out BareTrunk known)) return known;

            BareTrunk bare = null;
            SpeciesAsset.BareTrunk source = look >= 0 && look < lookSpecies.Length ? species[lookSpecies[look]].BareTrunkOf(lookModel[look]) : null;
            if (source != null)
            {
                var kept = new BarePart[source.Parts.Length];
                for (int i = 0; i < kept.Length; i++)
                {
                    Mesh mesh = lookParts[look][source.Parts[i]].mesh;
                    kept[i] = new BarePart
                    {
                        Part = source.Parts[i],
                        Boxes = source.Boxes[i],
                        Vertices = mesh.vertices,
                        Normals = mesh.normals,
                        Uvs = mesh.uv,
                        Triangles = mesh.triangles
                    };
                }

                bare = new BareTrunk { Parts = kept };
            }

            bareTrunks[look] = bare;
            return bare;
        }

        // A bare trunk's meshes, part by part, clipped to its length key (BareKey): a box past it is left
        // out, one across it cut there (its vertices above brought down to it).
        private Mesh[] BareMeshesOf(long key)
        {
            if (bareMeshes.TryGetValue(key, out Mesh[] made)) return made;

            int look = (int)(key >> 32);
            float length = (uint)key * BareStep;
            BareTrunk bare = BareTrunkOf(look);
            var meshes = new Mesh[bare.Parts.Length];
            for (int i = 0; i < bare.Parts.Length; i++)
            {
                BarePart source = bare.Parts[i];
                SpeciesAsset.Part part = lookParts[look][source.Part];
                float limit = part.scale.y != 0f ? (length - part.offset.y) / part.scale.y : float.MaxValue;
                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var triangles = new List<int>();
                foreach (int box in source.Boxes)
                {
                    float bottom = float.MaxValue;
                    for (int v = 0; v < 24; v++) bottom = Mathf.Min(bottom, source.Vertices[box * 24 + v].y);
                    if (bottom >= limit) continue;

                    int start = vertices.Count;
                    for (int v = 0; v < 24; v++)
                    {
                        Vector3 vertex = source.Vertices[box * 24 + v];
                        vertex.y = Mathf.Min(vertex.y, limit);
                        vertices.Add(vertex);
                        normals.Add(source.Normals.Length > 0 ? source.Normals[box * 24 + v] : Vector3.up);
                        uvs.Add(source.Uvs.Length > 0 ? source.Uvs[box * 24 + v] : Vector2.zero);
                    }

                    for (int t = 0; t < 36; t++) triangles.Add(source.Triangles[box * 36 + t] - box * 24 + start);
                }

                var mesh = new Mesh { name = "Bare trunk" };
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                meshes[i] = mesh;
            }

            bareMeshes[key] = meshes;
            return meshes;
        }

        private Fall NewFall(int look, PlantView plant, float yaw)
        {
            var direction = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            float size = PlantShape.Scale(plant.Stage);
            float cut = PlantShape.StumpHeight * size;
            float trunkHalf = look < trunkHalves.Length && trunkHalves[look] > 0f ? trunkHalves[look] * size : 0.15f * size;
            return new Fall
            {
                Look = look,
                FruitLook = FruitLookOf(look, plant),
                Foot = FootOf(plant),
                Turn = TurnOf(plant.X, plant.Z),
                Size = size,
                Axis = Vector3.Cross(Vector3.up, direction),
                Direction = direction,
                Slide = LyingFootDistance + cut,
                Drop = Mathf.Max(0f, cut - trunkHalf),
                BareKey = -1,
                Started = Time.time,
                Shown = Time.time,
                X = plant.X,
                Z = plant.Z
            };
        }

        // Where a plant's stump will be cut: the middle of its trunk there (the widest trunk box at that
        // height, which its stump's cut disc covers), the trunk's half extents along x and z, and the
        // cut's material. False for a plant whose model leaves no stump.
        public bool TryGetTrunk(PlantView plant, out Vector3 centre, out Vector2 halfSize, out Material cutMaterial)
        {
            centre = default;
            halfSize = default;
            cutMaterial = null;
            plant.Stump = false;
            int look = LookOf(plant);
            if (look < 0 || stumpLooks[look] < 0) return false;

            // The disc is the stump's last part, CutDiscShare of the trunk's width (PlantModelBuilder.StumpOf).
            IReadOnlyList<SpeciesAsset.Part> stump = lookParts[stumpLooks[look]];
            SpeciesAsset.Part cut = stump[stump.Count - 1];
            Bounds bounds = cut.mesh.bounds;
            Vector3 middle = cut.offset + Vector3.Scale(bounds.center, cut.scale);
            Vector3 extents = Vector3.Scale(bounds.extents, cut.scale) / CutDiscShare;

            float size = PlantShape.Scale(plant.Stage);
            Quaternion turn = TurnOf(plant.X, plant.Z);
            Vector3 foot = FootOf(plant);
            centre = foot + turn * new Vector3(middle.x, 0f, middle.z) * size + Vector3.up * (PlantShape.StumpHeight * size);
            Vector3 turned = turn * extents;
            halfSize = new Vector2(Mathf.Abs(turned.x), Mathf.Abs(turned.z)) * size;
            cutMaterial = cut.material;
            return true;
        }

        // To call every frame. Plants beyond viewDistance are not drawn.
        public void Render(Vector3 cameraPosition, float viewDistance)
        {
            int minX = Mathf.FloorToInt((cameraPosition.x - viewDistance) / RegionSize);
            int maxX = Mathf.FloorToInt((cameraPosition.x + viewDistance) / RegionSize);
            int minZ = Mathf.FloorToInt((cameraPosition.z - viewDistance) / RegionSize);
            int maxZ = Mathf.FloorToInt((cameraPosition.z + viewDistance) / RegionSize);

            for (int rz = minZ; rz <= maxZ; rz++)
            {
                for (int rx = minX; rx <= maxX; rx++)
                {
                    if (!regions.TryGetValue(KeyOf(rx, rz), out Region region) || region.Plants.Count == 0) continue;
                    if (region.DistanceSquaredTo(cameraPosition) > viewDistance * viewDistance) continue;

                    if (region.Dirty) region.Rebuild(this);
                    Draw(region);
                }
            }

            DrawFalls(cameraPosition);
        }

        // Each falling tree: flat after FallSeconds, faster and faster, a small bounce, a moment lying,
        // then it shrinks away about its middle (its logs lie there by then).
        private void DrawFalls(Vector3 cameraPosition)
        {
            float now = Time.time;
            for (int i = falls.Count - 1; i >= 0; i--)
            {
                Fall fall = falls[i];
                float t = now - fall.Started;
                float end = FallSeconds + BounceSeconds + LieSeconds + ShrinkSeconds;

                // A tree left lying (ShowLying) stays as it lies once its bounce is over.
                if (fall.LyingId != 0) t = Mathf.Min(t, FallSeconds + BounceSeconds);
                if (t >= end)
                {
                    falls.RemoveAt(i);
                    continue;
                }

                if ((fall.Foot - cameraPosition).sqrMagnitude > FallViewDistance * FallViewDistance) continue;

                float angle;
                if (t < FallSeconds) angle = 90f * (t / FallSeconds) * (t / FallSeconds);
                else if (t < FallSeconds + BounceSeconds) angle = 90f - BounceDegrees * Mathf.Sin(Mathf.PI * (t - FallSeconds) / BounceSeconds);
                else angle = 90f;

                float shrinkStart = FallSeconds + BounceSeconds + LieSeconds;
                float shrink = t > shrinkStart ? 1f - (t - shrinkStart) / ShrinkSeconds : 1f;

                Quaternion fallen = Quaternion.AngleAxis(angle, fall.Axis);
                float cut = PlantShape.StumpHeight * fall.Size;

                // Down, it slides off its stump onto the ground over its bounce.
                float slid = t <= FallSeconds ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - FallSeconds) / BounceSeconds));
                Vector3 pivot = fall.Foot + Vector3.up * cut + slid * (fall.Direction * fall.Slide - Vector3.up * fall.Drop);
                if (fall.BareKey >= 0)
                {
                    DrawBare(fall, fallen, pivot, cut);
                    continue;
                }

                DrawFallen(fall.Look, fall, fallen, pivot, cut, shrink);
                if (fall.FruitLook >= 0) DrawFallen(fall.FruitLook, fall, fallen, pivot, cut, shrink);
            }
        }

        private void DrawFallen(int look, Fall fall, Quaternion fallen, Vector3 pivot, float cut, float shrink)
        {
            // Shrinking about the middle of the trunk lying along the ground.
            float middle = (lookTops[look] * fall.Size - cut) * 0.5f;
            Vector3 centre = pivot + fallen * (Vector3.up * middle);
            IReadOnlyList<SpeciesAsset.Part> parts = lookParts[look];
            for (int p = 0; p < parts.Count; p++)
            {
                Vector3 local = fall.Turn * parts[p].offset * fall.Size - Vector3.up * cut;
                Vector3 position = pivot + fallen * local;
                position = centre + (position - centre) * shrink;
                Matrix4x4 matrix = Matrix4x4.TRS(position, fallen * fall.Turn, parts[p].scale * (fall.Size * shrink));
                Graphics.RenderMesh(renderParams[look][p], parts[p].mesh, 0, matrix);
            }
        }

        // A pruned tree: its bare trunk, clipped to the logs it still holds.
        private void DrawBare(Fall fall, Quaternion fallen, Vector3 pivot, float cut)
        {
            BareTrunk bare = BareTrunkOf(fall.Look);
            if (bare == null) return;

            Mesh[] meshes = BareMeshesOf(fall.BareKey);
            IReadOnlyList<SpeciesAsset.Part> parts = lookParts[fall.Look];
            for (int i = 0; i < bare.Parts.Length; i++)
            {
                if (meshes[i].vertexCount == 0) continue;

                SpeciesAsset.Part part = parts[bare.Parts[i].Part];
                Vector3 local = fall.Turn * part.offset * fall.Size - Vector3.up * cut;
                Matrix4x4 matrix = Matrix4x4.TRS(pivot + fallen * local, fallen * fall.Turn, part.scale * fall.Size);
                Graphics.RenderMesh(renderParams[fall.Look][bare.Parts[i].Part], meshes[i], 0, matrix);
            }
        }

        private void Draw(Region region)
        {
            foreach (int look in region.Looks)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = lookParts[look];
                for (int p = 0; p < parts.Count; p++)
                {
                    // Each batch carries the box of its region: Unity itself culls those out of view.
                    RenderParams parameters = renderParams[look][p];
                    parameters.worldBounds = region.Bounds;

                    for (int start = 0; start < region.Counts[look]; start += MaxInstancesPerCall)
                    {
                        int count = Mathf.Min(MaxInstancesPerCall, region.Counts[look] - start);
                        Graphics.RenderMeshInstanced(parameters, parts[p].mesh, 0, region.Matrices[look][p], count, start);
                    }
                }
            }
        }

        // The look a plant shows (its model's stump for a stump), or -1 if it shows none.
        private int LookOf(PlantView plant)
        {
            SpeciesAsset asset = species[plant.SpeciesIndex];
            if (asset.ModelCount == 0) return -1;

            int look = firstLook[plant.SpeciesIndex] + asset.ModelAt(plant.X, plant.Z);
            return plant.Stump ? stumpLooks[look] : look;
        }

        // The look of the fruit a plant of this look bears, or -1 if it bears none (not ripe).
        private int FruitLookOf(int look, PlantView plant)
        {
            if (plant.Stump || plant.RipeStage < 1 || plant.RipeStage > Plant.RipeStageCount) return -1;

            return fruitLooks[look][plant.RipeStage - 1];
        }

        private static long KeyOf(int regionX, int regionZ) => ((long)regionX << 32) | (uint)regionZ;

        private static Quaternion TurnOf(int x, int z)
        {
            int hash = (x * 73856093) ^ (z * 19349663);
            return Turns[(hash >> 7) & 3];
        }

        private struct Fall
        {
            public int Look;
            public int FruitLook;
            public Vector3 Foot;
            public Quaternion Turn;
            public float Size;

            // About which it falls: horizontal, across the way it falls; the way it falls; how far its
            // pivot slides along it once down (its foot then lies at LyingFootDistance, off its stump)
            // and how far it drops (onto the ground).
            public Vector3 Axis;
            public Vector3 Direction;
            public float Slide;
            public float Drop;

            // Pruned: its bare trunk's look and length (BareMeshesOf), else -1.
            public long BareKey;
            public float Started;

            // When it was added, the column it grew in, and the fallen tree it stays lying for (0: none,
            // it shrinks away).
            public float Shown;
            public int X;
            public int Z;
            public int LyingId;
        }

        // A model's trunk without its leaves and branches: per part of its wood, the boxes kept and the
        // part mesh's data, to clip from.
        private sealed class BareTrunk
        {
            public BarePart[] Parts;
        }

        private sealed class BarePart
        {
            public int Part;
            public int[] Boxes;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector2[] Uvs;
            public int[] Triangles;
        }

        private sealed class Region
        {
            public readonly Dictionary<int, PlantView> Plants = new Dictionary<int, PlantView>();

            // By look then by part: the matrices of the region's plants, recomputed when Dirty is true.
            public readonly Matrix4x4[][][] Matrices;
            public readonly int[] Counts;

            // The looks the region shows, and the box of its plants (Rebuild): Unity culls each batch by
            // it, for the camera and for each shadow cascade.
            public readonly List<int> Looks = new List<int>();
            public Bounds Bounds;
            public bool Dirty = true;

            private readonly float minX;
            private readonly float minZ;

            public Region(int regionX, int regionZ, IReadOnlyList<SpeciesAsset.Part>[] lookParts)
            {
                minX = regionX * RegionSize;
                minZ = regionZ * RegionSize;

                Counts = new int[lookParts.Length];
                Matrices = new Matrix4x4[lookParts.Length][][];
                for (int look = 0; look < lookParts.Length; look++)
                {
                    Matrices[look] = new Matrix4x4[lookParts[look].Count][];
                    for (int p = 0; p < Matrices[look].Length; p++)
                    {
                        Matrices[look][p] = new Matrix4x4[0];
                    }
                }
            }

            // Horizontal distance between a point and the region's rectangle.
            public float DistanceSquaredTo(Vector3 point)
            {
                float dx = Mathf.Max(0f, Mathf.Max(minX - point.x, point.x - (minX + RegionSize)));
                float dz = Mathf.Max(0f, Mathf.Max(minZ - point.z, point.z - (minZ + RegionSize)));
                return dx * dx + dz * dz;
            }

            public void Rebuild(PlantRenderer renderer)
            {
                for (int look = 0; look < Counts.Length; look++)
                {
                    Counts[look] = 0;
                }

                foreach (PlantView plant in Plants.Values)
                {
                    int look = renderer.LookOf(plant);
                    if (look < 0) continue;

                    Counts[look]++;
                    int fruitLook = renderer.FruitLookOf(look, plant);
                    if (fruitLook >= 0) Counts[fruitLook]++;
                }

                Looks.Clear();
                for (int look = 0; look < Counts.Length; look++)
                {
                    if (Counts[look] > 0) Looks.Add(look);
                    for (int p = 0; p < Matrices[look].Length; p++)
                    {
                        if (Matrices[look][p].Length < Counts[look]) Matrices[look][p] = new Matrix4x4[Mathf.NextPowerOfTwo(Counts[look])];
                    }

                    Counts[look] = 0;
                }

                float bottom = float.MaxValue;
                float top = float.MinValue;
                foreach (PlantView plant in Plants.Values)
                {
                    int look = renderer.LookOf(plant);
                    if (look < 0) continue;

                    float size = PlantShape.Scale(plant.Stage);
                    Vector3 foot = renderer.FootOf(plant);
                    Quaternion turn = TurnOf(plant.X, plant.Z);
                    Place(renderer, look, foot, turn, size);
                    bottom = Mathf.Min(bottom, foot.y + renderer.lookBottoms[look] * size);
                    top = Mathf.Max(top, foot.y + renderer.lookTops[look] * size);

                    int fruitLook = renderer.FruitLookOf(look, plant);
                    if (fruitLook < 0) continue;

                    Place(renderer, fruitLook, foot, turn, size);
                    bottom = Mathf.Min(bottom, foot.y + renderer.lookBottoms[fruitLook] * size);
                    top = Mathf.Max(top, foot.y + renderer.lookTops[fruitLook] * size);
                }

                // A block of margin below and above: the box only has to hold the plants.
                if (bottom > top)
                {
                    bottom = 0f;
                    top = 0f;
                }

                float height = top - bottom + 3f;
                var centre = new Vector3(minX + RegionSize * 0.5f, bottom - 1f + height * 0.5f, minZ + RegionSize * 0.5f);
                Bounds = new Bounds(centre, new Vector3(RegionSize + 2f * RegionMargin, height, RegionSize + 2f * RegionMargin));
                Dirty = false;
            }

            private void Place(PlantRenderer renderer, int look, Vector3 foot, Quaternion turn, float size)
            {
                IReadOnlyList<SpeciesAsset.Part> parts = renderer.lookParts[look];
                for (int p = 0; p < parts.Count; p++)
                {
                    Matrices[look][p][Counts[look]] = Matrix4x4.TRS(foot + turn * parts[p].offset * size, turn, parts[p].scale * size);
                }

                Counts[look]++;
            }
        }
    }
}
