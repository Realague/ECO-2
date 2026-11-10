using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Claimed land shown in the world (display only): the ground of every claimed plot near the camera
    // tinted in its owner's colour as the maps draw it (ClaimLayer.ColourOf: pale gold yours, green
    // allowed, a hue from the owner's name for others), down the sides of its steps too, with a brighter
    // line along its edges where it meets land that is not that owner's. Shown or hidden with C while
    // playing, or from the full-screen map's plot mode (Shown, kept between sessions). One mesh per
    // plot, made when it first shows and again when a block changes in or beside it or the plot
    // changes hands; drawn with an unlit translucent material (ECO/ArrowPreview, white).
    public class ClaimOverlay : MonoBehaviour
    {
        private const string PrefsKey = "showClaims";

        // How far from the camera plots are drawn, in blocks.
        private const float Range = 80f;

        // Over the ground, so as not to flicker with it.
        private const float Lift = 0.04f;
        private const byte FillAlpha = 60;
        private const byte EdgeAlpha = 220;
        private const float EdgeWidth = 0.14f;

        [SerializeField] private ClaimSync claimSync;
        [SerializeField] private WorldSync worldSync;
        [SerializeField] private HotbarHud hotbar;
        [SerializeField] private MapScreen mapScreen;
        [SerializeField] private InventoryScreen inventoryScreen;
        [SerializeField] private Material material;

        private readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        private readonly HashSet<int> dirty = new HashSet<int>();
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Color32> colours = new List<Color32>();
        private readonly List<int> triangles = new List<int>();
        private World world;
        private RenderParams renderParams;

        // Whether the ground is drawn smoothed (TerrainSettings): the tint then lies on its vertices.
        private bool smooth;

        public static bool Shown { get; private set; }

        public static void SetShown(bool shown)
        {
            Shown = shown;
            PlayerPrefs.SetInt(PrefsKey, shown ? 1 : 0);
        }

        private void Awake()
        {
            Shown = PlayerPrefs.GetInt(PrefsKey, 0) == 1;
            renderParams = new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        }

        private void OnEnable()
        {
            if (claimSync != null) claimSync.PlotChanged += OnPlotChanged;
        }

        private void OnDisable()
        {
            if (claimSync != null) claimSync.PlotChanged -= OnPlotChanged;
            Watch(null);
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            bool playing = worldSync.World != null && UnityEngine.Cursor.lockState == CursorLockMode.Locked
                && !CommandConsole.HoldsInput && !StorageScreen.HoldsInput && !mapScreen.HoldsInput && !inventoryScreen.HoldsInput;
            if (keyboard == null || !playing || !(keyboard.FindKeyOnCurrentKeyboardLayout("c") ?? keyboard.cKey).wasPressedThisFrame) return;

            SetShown(!Shown);
            hotbar.ShowMessage(Shown ? "Claimed land shown (C)" : "Claimed land hidden (C)");
        }

        private void LateUpdate()
        {
            Watch(worldSync.World);
            if (smooth != TerrainSettings.Smooth)
            {
                smooth = TerrainSettings.Smooth;
                dirty.UnionWith(meshes.Keys);
            }

            Camera eye = Camera.main;
            if (!Shown || world == null || eye == null || material == null || claimSync.PlotsX == 0) return;

            Vector3 at = eye.transform.position;
            int size = ClaimRules.PlotSize;
            int fromX = Mathf.Max(0, Mathf.FloorToInt((at.x - Range) / size));
            int toX = Mathf.Min(claimSync.PlotsX - 1, Mathf.FloorToInt((at.x + Range) / size));
            int fromZ = Mathf.Max(0, Mathf.FloorToInt((at.z - Range) / size));
            int toZ = Mathf.Min(claimSync.PlotsZ - 1, Mathf.FloorToInt((at.z + Range) / size));
            for (int plotZ = fromZ; plotZ <= toZ; plotZ++)
            {
                for (int plotX = fromX; plotX <= toX; plotX++)
                {
                    int owner = claimSync.ShownOwnerIndex(plotX, plotZ);
                    if (owner < 0) continue;

                    int index = plotZ * claimSync.PlotsX + plotX;
                    if (!meshes.TryGetValue(index, out Mesh mesh) || dirty.Remove(index))
                    {
                        mesh = Build(plotX, plotZ, owner, mesh);
                        meshes[index] = mesh;
                    }

                    Graphics.RenderMesh(renderParams, mesh, 0, Matrix4x4.identity);
                }
            }
        }

        // Follows the world shown: its block changes remake the plots they touch; another world, none kept.
        private void Watch(World shown)
        {
            if (shown == world) return;

            if (world != null)
            {
                world.BlockChanged -= OnBlockChanged;
                world.Pins.Changed -= OnPinChanged;
            }

            world = shown;
            if (world != null)
            {
                world.BlockChanged += OnBlockChanged;
                world.Pins.Changed += OnPinChanged;
            }
            foreach (Mesh mesh in meshes.Values)
            {
                Destroy(mesh);
            }

            meshes.Clear();
            dirty.Clear();
        }

        // Storage placed or taken (World.Pins) moves the smoothed ground around it as a block would.
        private void OnPinChanged(BlockPos cell) => OnBlockChanged(cell, 0, 0);

        private void OnBlockChanged(BlockPos position, ushort before, ushort after)
        {
            // The column's plot, and its neighbour's when it lies on an edge (a step's side).
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    MarkDirty(ClaimRules.PlotOf(position.X + dx), ClaimRules.PlotOf(position.Z + dz));
                }
            }
        }

        // A plot changed hands: it and its neighbours (their edges); -1, -1: every plot.
        private void OnPlotChanged(int plotX, int plotZ)
        {
            if (plotX < 0)
            {
                dirty.UnionWith(meshes.Keys);
                return;
            }

            MarkDirty(plotX, plotZ);
            MarkDirty(plotX - 1, plotZ);
            MarkDirty(plotX + 1, plotZ);
            MarkDirty(plotX, plotZ - 1);
            MarkDirty(plotX, plotZ + 1);
        }

        private void MarkDirty(int plotX, int plotZ)
        {
            if (plotX < 0 || plotZ < 0 || plotX >= claimSync.PlotsX || plotZ >= claimSync.PlotsZ) return;

            dirty.Add(plotZ * claimSync.PlotsX + plotX);
        }

        private Mesh Build(int plotX, int plotZ, int owner, Mesh mesh)
        {
            claimSync.TryGetShownOwner(owner, out string name, out ClaimRelation relation);
            Color32 colour = ClaimLayer.ColourOf(name, relation);
            var fill = new Color32(colour.r, colour.g, colour.b, FillAlpha);
            var edge = new Color32(colour.r, colour.g, colour.b, EdgeAlpha);
            vertices.Clear();
            colours.Clear();
            triangles.Clear();

            int size = ClaimRules.PlotSize;
            for (int z = plotZ * size; z < (plotZ + 1) * size; z++)
            {
                for (int x = plotX * size; x < (plotX + 1) * size; x++)
                {
                    float h = SurfaceOf(x, z);
                    Quad(Corner(x, h, z), Corner(x, h, z + 1), Corner(x + 1, h, z + 1), Corner(x + 1, h, z), fill);

                    // The sides of a step down to a neighbour, and the edges of the owner's land.
                    Side(x, z, h, 1, 0, owner, fill, edge);
                    Side(x, z, h, -1, 0, owner, fill, edge);
                    Side(x, z, h, 0, 1, owner, fill, edge);
                    Side(x, z, h, 0, -1, owner, fill, edge);
                }
            }

            if (mesh == null) mesh = new Mesh { name = "ClaimOverlay" };
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Toward the neighbour (dx, dz): the step's side down to it, and an edge line if its land is
        // not this owner's.
        private void Side(int x, int z, float h, int dx, int dz, int owner, Color32 fill, Color32 edge)
        {
            int nx = x + dx;
            int nz = z + dz;
            bool inside = world.InBounds(nx, 0, nz);
            float nh = inside ? SurfaceOf(nx, nz) : h;

            // The face between the two columns, from the far side's corners.
            float fx = dx > 0 ? x + 1 : x;
            float fz = dz > 0 ? z + 1 : z;
            Vector3 a = dx != 0 ? new Vector3(fx, 0f, z) : new Vector3(x, 0f, fz);
            Vector3 b = dx != 0 ? new Vector3(fx, 0f, z + 1) : new Vector3(x + 1, 0f, fz);
            if (nh < h - 0.01f)
            {
                // On smoothed ground, a block at a time, through the vertices of each level.
                if (smooth && IsWhole(nh) && IsWhole(h))
                {
                    for (float y = nh; y < h - 0.5f; y += 1f)
                    {
                        Quad(Corner(a.x, y, a.z), Corner(a.x, y + 1f, a.z), Corner(b.x, y + 1f, b.z), Corner(b.x, y, b.z), fill);
                    }
                }
                else
                {
                    Quad(Corner(a.x, nh, a.z), Corner(a.x, h, a.z), Corner(b.x, h, b.z), Corner(b.x, nh, b.z), fill);
                }
            }

            int neighbourOwner = inside ? claimSync.ShownOwnerIndex(ClaimRules.PlotOf(nx), ClaimRules.PlotOf(nz)) : -1;
            if (neighbourOwner == owner) return;

            // A strip along that edge, on this column's top.
            Vector3 inward = new Vector3(-dx, 0f, -dz) * EdgeWidth;
            Vector3 lift = Vector3.up * 0.005f;
            Vector3 from = Corner(a.x, h, a.z) + lift;
            Vector3 to = Corner(b.x, h, b.z) + lift;
            Quad(from, to, to + inward, from + inward, edge);
        }

        // Whether a drawn height is a block's top (not a water surface).
        private static bool IsWhole(float h) => Mathf.Abs(h - Lift - Mathf.Round(h - Lift)) < 0.001f;

        // A point of the ground at a block corner (x, h, z), h as drawn (Lift included): on smoothed
        // ground (TerrainSettings), the vertex the ground shows there (SmoothTerrain), lifted along its
        // normal; otherwise, and on water, as given.
        private Vector3 Corner(float x, float h, float z)
        {
            var given = new Vector3(x, h, z);
            if (!smooth || !IsWhole(h)) return given;

            int ix = Mathf.RoundToInt(x), iy = Mathf.RoundToInt(h - Lift), iz = Mathf.RoundToInt(z);
            int mask = SmoothTerrain.CornerMask(world, ix, iy, iz);
            if (mask == 0 || mask == 0xFF) return given;

            SmoothTerrain.CornerVertex(world, ix, iy, iz, out System.Numerics.Vector3 position, out System.Numerics.Vector3 normal);
            Vector3 outward = normal == System.Numerics.Vector3.Zero ? Vector3.up : normal.ToUnity();
            return position.ToUnity() + outward * Lift;
        }

        // Where a column's top is drawn: its solid top, or the surface of the water on it.
        private float SurfaceOf(int x, int z)
        {
            int top = world.GetTopHeight(x, z);
            float h = top > 0 && world.IsWater(x, top - 1, z) ? top - 1 + ChunkMesher.WaterSurface : top;
            return h + Lift;
        }

        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 colour)
        {
            int first = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            for (int i = 0; i < 4; i++)
            {
                colours.Add(colour);
            }

            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
        }
    }
}
