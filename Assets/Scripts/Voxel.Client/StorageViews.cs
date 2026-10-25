using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Draws placed storage, without objects. A chest is a box of planks with iron bands and a clasp
    // (ChestShapes), its front toward where its owner stood, its lid raised while anyone has it open.
    // A stockpile (3 x 3 cells, or 2 x 2 for a small one) is a low frame of planks around its cells,
    // with each pile's blocks as small cubes of a third of a block, 9 to a layer, up to 3 layers (a
    // full pile of a large stockpile fills its cell, a small one's 20 stop on the third), in the blocks'
    // tiles (the blocks' material, fully lit) or, for wood, in its tree's bark; its mesh is made again only
    // when its piles change. Also draws where storage held in hand would go (green, or red where it
    // cannot), as an outline.
    internal sealed class StorageViews
    {
        // The stockpile's frame stands this high around it.
        public const float FrameHeight = 0.12f;

        private const float FrameThickness = 0.08f;
        private const float CubeSize = 0.3f;

        // A pile's cubes stand 3 by 3 in its cell, 9 to a layer.
        private const int CubesAcross = 3;
        private const int CubesPerLayer = CubesAcross * CubesAcross;
        private const float LidOpenDegrees = 65f;
        private const float LidDegreesPerSecond = 220f;
        private const float OutlineThickness = 0.03f;

        private readonly RenderParams wood;
        private readonly RenderParams iron;
        private readonly RenderParams blocks;
        private readonly Material woodMaterial;
        private readonly IReadOnlyList<SpeciesAsset> plants;
        private readonly Dictionary<int, RenderParams> barks = new Dictionary<int, RenderParams>();
        private readonly RenderParams validOutline;
        private readonly RenderParams invalidOutline;
        private readonly Mesh chestBody;
        private readonly Mesh chestLid;
        private readonly Mesh frame;
        private readonly Mesh smallFrame;
        private readonly Mesh outline;
        private readonly Dictionary<int, float> lidAngles = new Dictionary<int, float>();
        private readonly Dictionary<int, Pile> piles = new Dictionary<int, Pile>();
        private readonly HashSet<int> seen = new HashSet<int>();
        private readonly List<int> gone = new List<int>();
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<Color32> colours = new List<Color32>();
        private readonly List<int> blockTriangles = new List<int>();
        private readonly SortedDictionary<int, List<int>> woodTriangles = new SortedDictionary<int, List<int>>();

        // A stockpile's mesh: blocks in its first submesh, then each species' wood in one of its own.
        private sealed class Pile
        {
            public long Key;
            public Mesh Mesh;
            public readonly List<int> WoodSpecies = new List<int>();
        }

        public StorageViews(Material woodMaterial, Material ironMaterial, Material blockMaterial, IReadOnlyList<SpeciesAsset> plants)
        {
            wood = Params(woodMaterial);
            iron = Params(ironMaterial);
            blocks = Params(blockMaterial);
            this.woodMaterial = woodMaterial;
            this.plants = plants;

            Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
            validOutline = OutlineParams(unlit, new Color(0.45f, 0.95f, 0.45f));
            invalidOutline = OutlineParams(unlit, new Color(0.95f, 0.35f, 0.3f));

            chestBody = BoxMesh.Build("ChestBody", new[] { ChestShapes.BodyWood, ChestShapes.BodyIron });
            chestLid = BoxMesh.Build("ChestLid", new[] { ChestShapes.LidWood, ChestShapes.LidIron });
            frame = BoxMesh.Build("StockpileFrame", new[] { FrameBoxes(StorageRules.StockpileSide) });
            smallFrame = BoxMesh.Build("SmallStockpileFrame", new[] { FrameBoxes(StorageRules.SmallStockpileSide) });
            outline = BoxMesh.Build("StorageOutline", new[] { OutlineBoxes() });
        }

        // How high a pile of this many blocks stands.
        public static float PileHeight(int count)
        {
            int layers = (count + CubesPerLayer - 1) / CubesPerLayer;
            return layers / 3f;
        }

        // Draws a storage this frame (open: someone has it open).
        public void Add(Storage storage, bool open)
        {
            seen.Add(storage.Id);
            if (storage.Kind == StorageKind.Chest) DrawChest(storage, open);
            else DrawStockpile(storage);
        }

        // Where held storage would go: a chest's cell, or a stockpile's cells from its corner.
        public void AddPreview(StorageKind kind, BlockPos position, bool valid)
        {
            RenderParams look = valid ? validOutline : invalidOutline;
            if (look.material == null) return;

            Vector3 size = kind == StorageKind.Chest
                ? new Vector3(1f - 2f * StorageSync.ChestInset, StorageSync.ChestHeight, 1f - 2f * StorageSync.ChestInset)
                : new Vector3(StorageRules.SideOf(kind), 1f / 3f, StorageRules.SideOf(kind));
            Vector3 corner = kind == StorageKind.Chest
                ? new Vector3(position.X + StorageSync.ChestInset, position.Y, position.Z + StorageSync.ChestInset)
                : new Vector3(position.X, position.Y, position.Z);
            Graphics.RenderMesh(look, outline, 0, Matrix4x4.TRS(corner, Quaternion.identity, size));
        }

        // To call once a frame after Add: forgets what is no longer drawn.
        public void Render()
        {
            gone.Clear();
            foreach (int id in piles.Keys)
            {
                if (!seen.Contains(id)) gone.Add(id);
            }

            foreach (int id in lidAngles.Keys)
            {
                if (!seen.Contains(id) && !piles.ContainsKey(id)) gone.Add(id);
            }

            foreach (int id in gone)
            {
                if (piles.TryGetValue(id, out Pile entry)) Object.Destroy(entry.Mesh);
                piles.Remove(id);
                lidAngles.Remove(id);
            }

            seen.Clear();
        }

        public void Clear()
        {
            foreach (Pile entry in piles.Values)
            {
                Object.Destroy(entry.Mesh);
            }

            piles.Clear();
            lidAngles.Clear();
        }

        private void DrawChest(Storage storage, bool open)
        {
            // Its front faces back toward where its owner stood.
            Quaternion turn = Quaternion.Euler(0f, storage.Facing * 90f + 180f, 0f);
            var foot = new Vector3(storage.Position.X + 0.5f, storage.Position.Y, storage.Position.Z + 0.5f);
            Matrix4x4 body = Matrix4x4.TRS(foot, turn, Vector3.one);
            Graphics.RenderMesh(wood, chestBody, 0, body);
            Graphics.RenderMesh(iron, chestBody, 1, body);

            lidAngles.TryGetValue(storage.Id, out float angle);
            angle = Mathf.MoveTowards(angle, open ? LidOpenDegrees : 0f, LidDegreesPerSecond * Time.deltaTime);
            lidAngles[storage.Id] = angle;
            Matrix4x4 lid = body * Matrix4x4.TRS(ChestShapes.Hinge, Quaternion.Euler(-angle, 0f, 0f), Vector3.one);
            Graphics.RenderMesh(wood, chestLid, 0, lid);
            Graphics.RenderMesh(iron, chestLid, 1, lid);
        }

        private void DrawStockpile(Storage storage)
        {
            var corner = new Vector3(storage.Position.X, storage.Position.Y, storage.Position.Z);
            Mesh border = storage.Kind == StorageKind.SmallStockpile ? smallFrame : frame;
            Graphics.RenderMesh(wood, border, 0, Matrix4x4.Translate(corner));

            long key = PilesKey(storage.Piles);
            if (!piles.TryGetValue(storage.Id, out Pile entry))
            {
                entry = new Pile { Key = key + 1, Mesh = new Mesh { name = "StockpileCubes" } };
                piles[storage.Id] = entry;
            }

            if (entry.Key != key)
            {
                BuildPiles(storage.Piles, StorageRules.SideOf(storage.Kind), entry);
                entry.Key = key;
            }

            if (entry.Mesh.vertexCount == 0) return;

            Matrix4x4 at = Matrix4x4.Translate(corner);
            if (blocks.material != null) Graphics.RenderMesh(blocks, entry.Mesh, 0, at);
            for (int i = 0; i < entry.WoodSpecies.Count; i++)
            {
                Graphics.RenderMesh(BarkOf(entry.WoodSpecies[i]), entry.Mesh, 1 + i, at);
            }
        }

        // Each pile's cubes, in the stockpile's own space (its corner at the origin): blocks in the
        // first submesh with their tiles, then each species' wood in a submesh of its own.
        private void BuildPiles(StockpilePiles stock, int side, Pile entry)
        {
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            colours.Clear();
            blockTriangles.Clear();
            foreach (List<int> list in woodTriangles.Values)
            {
                list.Clear();
            }

            for (int pile = 0; pile < stock.Count; pile++)
            {
                int item = stock.ItemAt(pile);
                int count = stock.CountAt(pile);
                int cellX = pile % side;
                int cellZ = pile / side;
                for (int i = 0; i < count; i++)
                {
                    int layer = i / CubesPerLayer;
                    int spot = i % CubesPerLayer;
                    var origin = new Vector3(
                        cellX + (spot % CubesAcross + 0.5f) / CubesAcross - CubeSize * 0.5f,
                        layer / 3f,
                        cellZ + (spot / CubesAcross + 0.5f) / CubesAcross - CubeSize * 0.5f);
                    AddCube(origin, item);
                }
            }

            entry.WoodSpecies.Clear();
            foreach (KeyValuePair<int, List<int>> wood in woodTriangles)
            {
                if (wood.Value.Count > 0) entry.WoodSpecies.Add(wood.Key);
            }

            Mesh mesh = entry.Mesh;
            mesh.Clear();
            mesh.subMeshCount = 1 + entry.WoodSpecies.Count;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colours);
            mesh.SetTriangles(blockTriangles, 0);
            for (int i = 0; i < entry.WoodSpecies.Count; i++)
            {
                mesh.SetTriangles(woodTriangles[entry.WoodSpecies[i]], 1 + i);
            }

            mesh.RecalculateBounds();
        }

        private void AddCube(Vector3 origin, int item)
        {
            bool isWood = Items.IsWood(item);
            List<int> triangles = blockTriangles;
            if (isWood && !woodTriangles.TryGetValue(Items.WoodSpecies(item), out triangles))
            {
                triangles = new List<int>();
                woodTriangles.Add(Items.WoodSpecies(item), triangles);
            }
            for (int face = 0; face < ChunkMesher.FaceCount; face++)
            {
                int first = vertices.Count;
                Vector3[] corners = ChunkMesher.FaceCorners[face];
                int tile = isWood ? 0 : BlockAtlas.TileFor(Items.BlockOf(item), face);
                for (int c = 0; c < corners.Length; c++)
                {
                    vertices.Add(origin + corners[c] * CubeSize);
                    normals.Add(ChunkMesher.FaceNormals[face]);
                    colours.Add(new Color32(255, 0, 0, 255));
                    Vector2 corner = ChunkMesher.CornerUvs[c];
                    if (isWood)
                    {
                        uvs.Add(corner * CubeSize);
                    }
                    else
                    {
                        float u = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, corner.x);
                        float v = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, corner.y);
                        uvs.Add(new Vector2((tile + u) / BlockAtlas.TileCount, v));
                    }
                }

                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first);
                triangles.Add(first + 2);
                triangles.Add(first + 3);
            }
        }

        // How a species' wood is drawn: its bark's grain in its wood's colour, else the planks'.
        private RenderParams BarkOf(int species)
        {
            if (!barks.TryGetValue(species, out RenderParams look))
            {
                Material bark = (uint)species < (uint)plants.Count ? plants[species].WoodMaterial : null;
                look = Params(bark != null ? bark : woodMaterial);
                barks.Add(species, look);
            }

            return look;
        }

        // What the piles hold, folded into a number that changes when they do.
        private static long PilesKey(StockpilePiles stock)
        {
            long key = 17;
            for (int pile = 0; pile < stock.Count; pile++)
            {
                key = key * 31 + stock.ItemAt(pile);
                key = key * 31 + stock.CountAt(pile);
            }

            return key;
        }

        // Planks just outside the stockpile's cells, so that its cubes never meet them.
        private static List<(Vector3 centre, Vector3 size)> FrameBoxes(float side)
        {
            float half = FrameThickness * 0.5f;
            float y = FrameHeight * 0.5f;
            return new List<(Vector3, Vector3)>
            {
                (new Vector3(side * 0.5f, y, -half), new Vector3(side + 2f * FrameThickness, FrameHeight, FrameThickness)),
                (new Vector3(side * 0.5f, y, side + half), new Vector3(side + 2f * FrameThickness, FrameHeight, FrameThickness)),
                (new Vector3(-half, y, side * 0.5f), new Vector3(FrameThickness, FrameHeight, side)),
                (new Vector3(side + half, y, side * 0.5f), new Vector3(FrameThickness, FrameHeight, side))
            };
        }

        // The twelve edges of a unit box from the origin, as thin bars (scaled to the box drawn).
        private static List<(Vector3 centre, Vector3 size)> OutlineBoxes()
        {
            float t = OutlineThickness;
            var bars = new List<(Vector3, Vector3)>();
            for (int a = 0; a <= 1; a++)
            {
                for (int b = 0; b <= 1; b++)
                {
                    bars.Add((new Vector3(0.5f, a, b), new Vector3(1f + t, t, t)));
                    bars.Add((new Vector3(a, 0.5f, b), new Vector3(t, 1f + t, t)));
                    bars.Add((new Vector3(a, b, 0.5f), new Vector3(t, t, 1f + t)));
                }
            }

            return bars;
        }

        private static RenderParams Params(Material material)
        {
            return new RenderParams(material) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
        }

        private static RenderParams OutlineParams(Shader shader, Color colour)
        {
            if (shader == null) return default;

            var material = new Material(shader) { name = "Storage outline" };
            material.SetColor("_BaseColor", colour);
            return new RenderParams(material) { shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
        }
    }
}
