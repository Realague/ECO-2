using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // Simple meshing: one face per block side that can be seen. Submesh 0 holds the opaque blocks,
    // drawn with the block atlas; submesh 1 holds the water, drawn with a transparent material. A
    // block's face is drawn when the block next to it is not opaque, so the bed and banks of a lake
    // show through its water; a water face is drawn against air or against open space, never
    // against other water. The top of water that has air above sits a little lower than a full
    // block, like a surface.
    //
    // Each vertex carries the sky light (SkyLight) in the red channel of its colour, 0 to 1, and the
    // light of torches (BlockLight) in the green one, 0 to 1 at Blocks.TorchLight. On the blocks'
    // faces both are smooth: each corner takes the mean level of the four cells in front of the face
    // around that corner, leaving out opaque ones; the blocks' shader darkens by the first and adds
    // a warm glow by the second. Torches and doors are not meshed (TorchViews and DoorViews draw them).
    // A rock holding an ore (Ores.OreOf) shows its own tile and carries the ore overlay's coordinates on
    // the fourth texture coordinates, flagged by the blue of their vertex colour, which the shader
    // lays over the rock.
    //
    // With smooth terrain (TerrainSettings), natural ground (SmoothTerrain.IsSmooth) goes on a third
    // submesh: each face of such a block that meets anything but smooth ground is a quad through the
    // smoothed vertices of its four corners, lit, coloured and textured at those corners (no texture
    // coordinates: the terrain material textures it from its position); faces of water and
    // construction blocks are then drawn against it, since it may lie half a block from them.
    //
    // The work is done by a ChunkMeshBuilder, which only reads the world and the light: builders run
    // on worker threads (ChunkMeshing), each with its own buffers, and only ApplyTo touches the Mesh.
    internal static class ChunkMesher
    {
        public const int TopFace = 2;
        public const int BottomFace = 3;

        public const int SolidSubmesh = 0;
        public const int WaterSubmesh = 1;

        // The smoothed ground (SmoothTerrain), drawn with the terrain material (the blocks' shader with
        // _ECO_SMOOTH): only meshes built with smooth terrain have it.
        public const int SmoothSubmesh = 2;

        // On the smoothed ground's vertices: the materials (SmoothTerrain.MaterialOf) of the quad's four
        // corners, the same on all four, and which corner the vertex is (one weight of 1), so that each
        // corner's share runs smoothly across the quad and into the next one.
        public const int SmoothMaterialChannel = 1;
        public const int SmoothWeightChannel = 2;

        public const int FaceCount = 6;
        public const float UvInset = 0.01f;

        // The texture coordinates of the ores' overlays (TEXCOORD3 in the blocks' shader).
        public const int OverlayUvChannel = 3;
        // Height of a water surface open to the air, above the bottom of its block.
        public const float WaterSurface = 0.875f;

        public static readonly Vector3Int[] FaceNormals =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1)
        };

        // The two axes along each face.
        public static readonly Vector3Int[] FaceAxisA =
        {
            new Vector3Int(0, 1, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 0),
            new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 0)
        };

        public static readonly Vector3Int[] FaceAxisB =
        {
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, 1), new Vector3Int(0, 0, 1),
            new Vector3Int(0, 1, 0), new Vector3Int(0, 1, 0)
        };

        // Four corners per face, clockwise seen from outside.
        // On the sides: bottom, top, top, bottom, so that the top of the tile stays up.
        public static readonly Vector3[][] FaceCorners =
        {
            new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1) },
            new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 0), new Vector3(0, 0, 0) },
            new[] { new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) },
            new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) },
            new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) }
        };

        public static readonly Vector2[] CornerUvs =
        {
            new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0)
        };
    }

    // Works out one chunk's mesh into its own buffers (Build, which only reads the world and the
    // light, so it may run on any thread while nothing changes them), then hands it to a Mesh
    // (ApplyTo, main thread only).
    internal sealed class ChunkMeshBuilder
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();

        // The ores' overlays (BlockAtlas.Overlays) on the fourth texture coordinates: the place in the
        // overlays' row on an ore's faces, which also have the blue of their vertex colour full (the
        // flag the shader reads: a mesh without these coordinates may hand the shader anything for
        // them, so they are never trusted alone). Filled up to the ore's faces only; the rest is zeros,
        // added in ApplyTo; empty when the chunk shows no ore, and then the mesh has no such coordinates.
        private readonly List<Vector2> overlayUvs = new List<Vector2>();
        private readonly List<Color32> colours = new List<Color32>();
        private readonly List<int> solidTriangles = new List<int>();
        private readonly List<int> waterTriangles = new List<int>();

        // The smoothed ground (SmoothTerrain): its triangles and, from its first vertex on, the corners'
        // materials and weights (zeros under the other vertices, added as needed and in ApplyTo).
        private readonly List<int> smoothTriangles = new List<int>();
        private readonly List<Vector4> smoothMaterials = new List<Vector4>();
        private readonly List<Vector4> smoothWeights = new List<Vector4>();

        // Whether the chunk being built shows natural ground smoothed.
        private bool smooth;

        // The smoothed vertices of the chunk's block corners (0 to Chunk.Size along each axis), worked
        // out once each while the chunk is built.
        private const int CornerSide = Chunk.Size + 1;
        private readonly bool[] cornerKnown = new bool[CornerSide * CornerSide * CornerSide];
        private readonly Vector3[] cornerPositions = new Vector3[CornerSide * CornerSide * CornerSide];
        private readonly Vector3[] cornerNormals = new Vector3[CornerSide * CornerSide * CornerSide];
        private readonly float[] cornerSky = new float[CornerSide * CornerSide * CornerSide];
        private readonly float[] cornerTorch = new float[CornerSide * CornerSide * CornerSide];
        private readonly int[] cornerMaterials = new int[CornerSide * CornerSide * CornerSide];
        private readonly int[] quadCorners = new int[4];
        private int originX;
        private int originY;
        private int originZ;

        // The cells in front of a face, 3 × 3 around the one it touches, indexed (a + 1) + 3 * (b + 1)
        // along the face's two axes.
        private readonly bool[] frontOpaque = new bool[9];
        private readonly int[] frontLevel = new int[9];
        private readonly int[] frontGlow = new int[9];
        private readonly float[] cornerLight = new float[4];
        private readonly float[] cornerGlow = new float[4];

        // The light of torches for the chunk being built; null for none.
        private BlockLight glow;

        // The tiles' variants and the ores' overlays for the chunk being built (BlockAtlas.Variants and
        // Overlays, read once per chunk).
        private TileVariants variants = TileVariants.None;
        private TileVariants overlays;

        // True when the last chunk built has no face to draw.
        public bool IsEmpty => vertices.Count == 0;

        // smoothTerrain: natural ground drawn smoothed (SmoothTerrain) on its own submesh; otherwise in
        // blocks, as it always was.
        public void Build(World world, SkyLight light, Chunk chunk, BlockLight blockLight = null, bool smoothTerrain = false)
        {
            glow = blockLight;
            smooth = smoothTerrain;
            variants = BlockAtlas.Variants;
            overlays = BlockAtlas.Overlays;
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            overlayUvs.Clear();
            colours.Clear();
            solidTriangles.Clear();
            waterTriangles.Clear();
            smoothTriangles.Clear();
            smoothMaterials.Clear();
            smoothWeights.Clear();
            if (chunk.IsEmpty) return;

            originX = chunk.Coord.X * Chunk.Size;
            originY = chunk.Coord.Y * Chunk.Size;
            originZ = chunk.Coord.Z * Chunk.Size;
            if (smooth) System.Array.Clear(cornerKnown, 0, cornerKnown.Length);

            for (int y = 0; y < Chunk.Size; y++)
            {
                for (int z = 0; z < Chunk.Size; z++)
                {
                    for (int x = 0; x < Chunk.Size; x++)
                    {
                        ushort block = chunk.Get(x, y, z);
                        ushort type = Blocks.TypeOf(block);
                        if (type == Blocks.Air || type == Blocks.Torch || type == Blocks.Door) continue;

                        int worldX = originX + x;
                        int worldY = originY + y;
                        int worldZ = originZ + z;
                        if (type == Blocks.Water)
                        {
                            AddWater(world, light, worldX, worldY, worldZ, new Vector3(x, y, z));
                            continue;
                        }

                        if (Shapes.IsShaped(block))
                        {
                            AddShape(world, light, block, new Vector3(x, y, z), new Vector3Int(worldX, worldY, worldZ));
                            continue;
                        }

                        // Ground under a player's block stays a block (SmoothTerrain.IsPinned), its faces
                        // hidden by the ground beside it as any block's are.
                        bool pinned = smooth && SmoothTerrain.IsSmooth(block) && SmoothTerrain.IsPinned(world, worldX, worldY, worldZ);
                        if (smooth && SmoothTerrain.IsSmooth(block) && !pinned)
                        {
                            AddSmoothBlock(world, light, block, x, y, z);
                            continue;
                        }

                        // A rock holding an ore shows the ore's overlay over its own tile.
                        OreKind ore = Ores.OreOf(block);
                        for (int face = 0; face < ChunkMesher.FaceCount; face++)
                        {
                            Vector3Int normal = ChunkMesher.FaceNormals[face];
                            int nx = worldX + normal.x, ny = worldY + normal.y, nz = worldZ + normal.z;
                            if (pinned ? world.IsOpaque(nx, ny, nz) : HidesFace(world, nx, ny, nz)) continue;

                            SampleFront(world, light, worldX + normal.x, worldY + normal.y, worldZ + normal.z, face);
                            AddFace(new Vector3(x, y, z), face, 1f, solidTriangles, true);
                            AddTileUvs(BlockAtlas.TileFor(block, face), worldX, worldY, worldZ, face);
                            if (ore != OreKind.None) AddOverlayUvs((int)ore - 1, worldX, worldY, worldZ, face);
                        }
                    }
                }
            }
        }

        public void ApplyTo(Mesh mesh)
        {
            // The vertex layout too: a mesh that had overlay coordinates must lose them if this chunk has none.
            mesh.Clear(false);
            mesh.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (overlayUvs.Count > 0)
            {
                while (overlayUvs.Count < vertices.Count) overlayUvs.Add(Vector2.zero);
                mesh.SetUVs(ChunkMesher.OverlayUvChannel, overlayUvs);
            }

            mesh.SetColors(colours);
            if (smooth)
            {
                while (smoothMaterials.Count < vertices.Count) smoothMaterials.Add(Vector4.zero);
                while (smoothWeights.Count < vertices.Count) smoothWeights.Add(Vector4.zero);
                mesh.SetUVs(ChunkMesher.SmoothMaterialChannel, smoothMaterials);
                mesh.SetUVs(ChunkMesher.SmoothWeightChannel, smoothWeights);
            }

            mesh.subMeshCount = smooth ? 3 : 2;
            mesh.SetTriangles(solidTriangles, ChunkMesher.SolidSubmesh);
            mesh.SetTriangles(waterTriangles, ChunkMesher.WaterSubmesh);
            if (smooth) mesh.SetTriangles(smoothTriangles, ChunkMesher.SmoothSubmesh);
        }

        // Whether a block's face against this cell is hidden by it: an opaque block hides it, except,
        // with smooth terrain, smooth ground, which may lie up to half a block away from the face (the
        // faces of water and construction blocks against it are drawn, so no gap shows between them);
        // ground kept in blocks under a player's block hides it as a block does.
        private bool HidesFace(World world, int x, int y, int z)
        {
            if (!world.IsOpaque(x, y, z)) return false;
            if (!smooth || !world.InBounds(x, y, z) || !SmoothTerrain.IsSmooth(world.GetBlock(x, y, z))) return true;
            return SmoothTerrain.IsPinned(world, x, y, z);
        }

        // A smooth block at (x, y, z) in the chunk: each face that does not meet other smooth ground, as a
        // quad through the smoothed vertices of its four corners (SmoothTerrain), lit at those corners.
        // The blue of its vertex colour is the ore its block holds (OreKind, 0 for none): an ore shows on
        // its own block's faces, whatever the rock around. Its alpha is the block's own material: the
        // shader shows it over most of the face and the corners' materials only near them, or a block
        // placed amid another ground (a stone in a field of dirt) took the material of the ground around
        // it, which is what most of its corners' cells hold.
        private void AddSmoothBlock(World world, SkyLight light, ushort block, int x, int y, int z)
        {
            byte ore = (byte)Ores.OreOf(block);
            byte own = (byte)SmoothTerrain.MaterialOf(block);
            for (int face = 0; face < ChunkMesher.FaceCount; face++)
            {
                Vector3Int n = ChunkMesher.FaceNormals[face];
                if (SmoothTerrain.IsSmoothAt(world, originX + x + n.x, originY + y + n.y, originZ + z + n.z)) continue;

                Vector3[] unit = ChunkMesher.FaceCorners[face];
                for (int i = 0; i < 4; i++)
                {
                    quadCorners[i] = Corner(world, light, x + (int)unit[i].x, y + (int)unit[i].y, z + (int)unit[i].z);
                    cornerLight[i] = cornerSky[quadCorners[i]];
                }

                var materials = new Vector4(
                    cornerMaterials[quadCorners[0]], cornerMaterials[quadCorners[1]],
                    cornerMaterials[quadCorners[2]], cornerMaterials[quadCorners[3]]);
                while (smoothMaterials.Count < vertices.Count) smoothMaterials.Add(Vector4.zero);
                while (smoothWeights.Count < vertices.Count) smoothWeights.Add(Vector4.zero);

                int first = vertices.Count;
                for (int i = 0; i < 4; i++)
                {
                    int corner = quadCorners[i];
                    vertices.Add(cornerPositions[corner]);
                    normals.Add(cornerNormals[corner] == Vector3.zero ? (Vector3)n : cornerNormals[corner]);
                    uvs.Add(Vector2.zero);
                    colours.Add(new Color32((byte)Mathf.RoundToInt(cornerSky[corner] * 255f), (byte)Mathf.RoundToInt(cornerTorch[corner] * 255f), ore, own));
                    smoothMaterials.Add(materials);
                    smoothWeights.Add(new Vector4(i == 0 ? 1f : 0f, i == 1 ? 1f : 0f, i == 2 ? 1f : 0f, i == 3 ? 1f : 0f));
                }

                // Split along the diagonal that keeps both triangles facing out (a quad bent across a
                // thin ledge folds over along the other); where both do, along the brighter one, as for
                // a block's face.
                Vector3 p0 = cornerPositions[quadCorners[0]], p1 = cornerPositions[quadCorners[1]];
                Vector3 p2 = cornerPositions[quadCorners[2]], p3 = cornerPositions[quadCorners[3]];
                float firstSplit = Mathf.Min(Facing(p0, p1, p2, n), Facing(p0, p2, p3, n));
                float otherSplit = Mathf.Min(Facing(p1, p2, p3, n), Facing(p1, p3, p0, n));
                bool brighterOther = cornerLight[0] + cornerLight[2] < cornerLight[1] + cornerLight[3];
                bool useOther = firstSplit > FoldLimit && otherSplit > FoldLimit ? brighterOther : otherSplit > firstSplit;
                int[] order = useOther ? OtherDiagonal : FirstDiagonal;
                foreach (int index in order)
                {
                    smoothTriangles.Add(first + index);
                }
            }
        }

        // A triangle facing out less than this (the cosine between it and its block's face) is taken as folded.
        private const float FoldLimit = 0.2f;

        // How much the triangle a, b, c (clockwise seen from outside) faces the way of its block's face:
        // the cosine between them, 0 for a degenerate one.
        private static float Facing(Vector3 a, Vector3 b, Vector3 c, Vector3Int face)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            float length = normal.magnitude;
            return length > 1e-6f ? Vector3.Dot(normal, face) / length : 0f;
        }

        private static readonly int[] FirstDiagonal = { 0, 1, 2, 0, 2, 3 };
        private static readonly int[] OtherDiagonal = { 1, 2, 3, 1, 3, 0 };

        // The index of a corner of the chunk (0 to Chunk.Size along each axis) in the corner arrays, worked
        // out the first time: its smoothed position in the chunk, its normal, its material, and its light,
        // the mean of the sky light and torch light of the cells around it that are not opaque (smooth
        // across faces and chunks; a shaped block that stops light borrows its neighbours', as for faces).
        private int Corner(World world, SkyLight light, int x, int y, int z)
        {
            int index = x + CornerSide * (y + CornerSide * z);
            if (cornerKnown[index]) return index;

            int cx = originX + x, cy = originY + y, cz = originZ + z;
            SmoothTerrain.CornerVertex(world, cx, cy, cz, out System.Numerics.Vector3 position, out System.Numerics.Vector3 normal);
            cornerPositions[index] = new Vector3(position.X - originX, position.Y - originY, position.Z - originZ);
            cornerNormals[index] = normal.ToUnity();
            int material = SmoothTerrain.MaterialAt(world, cx, cy, cz);
            cornerMaterials[index] = material >= 0 ? material : SmoothTerrain.MaterialOf(Blocks.Stone);

            int sum = 0, glowSum = 0, count = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int bx = cx - 1 + (bit & 1), by = cy - 1 + ((bit >> 1) & 1), bz = cz - 1 + ((bit >> 2) & 1);
                if (world.IsOpaque(bx, by, bz)) continue;

                int level = light.Get(bx, by, bz);
                int torch = glow != null ? glow.Get(bx, by, bz) : 0;
                if (level == 0 && Blocks.BlocksLight(world.GetBlock(bx, by, bz))) BorrowLight(world, light, bx, by, bz, out level, out torch);

                sum += level;
                glowSum += torch;
                count++;
            }

            cornerSky[index] = count > 0 ? sum / (count * (float)SkyLight.Max) : 0f;
            cornerTorch[index] = count > 0 ? glowSum / (count * (float)Blocks.TorchLight) : 0f;
            cornerKnown[index] = true;
            return index;
        }

        private void AddWater(World world, SkyLight light, int x, int y, int z, Vector3 blockOrigin)
        {
            // Water under air shows a surface a little below the top of its block.
            float top = world.IsWater(x, y + 1, z) ? 1f : ChunkMesher.WaterSurface;

            for (int face = 0; face < ChunkMesher.FaceCount; face++)
            {
                Vector3Int normal = ChunkMesher.FaceNormals[face];
                int nx = x + normal.x;
                int ny = y + normal.y;
                int nz = z + normal.z;
                // Any opaque block hides a water face, smooth ground too: the ground draws its own faces
                // toward the water (and keeps its corners at the surface on the grid), so a water face
                // there would only lie inside the bed or a bank, translucent, and show through the
                // surface as staircases over every lake.
                if (world.IsOpaque(nx, ny, nz) || world.IsWater(nx, ny, nz)) continue;

                // A water face is lit by the air in front of it: open sky over a lake, darkness in a cave.
                float level = light.Get(nx, ny, nz) / (float)SkyLight.Max;
                float torchLevel = GlowAt(nx, ny, nz);
                for (int corner = 0; corner < 4; corner++)
                {
                    cornerLight[corner] = level;
                    cornerGlow[corner] = torchLevel;
                }

                AddFace(blockOrigin, face, top, waterTriangles, false);
                for (int corner = 0; corner < 4; corner++)
                {
                    uvs.Add(ChunkMesher.CornerUvs[corner]);
                }
            }
        }

        // The brightest sky light and torch light of a cell's six neighbours that let light through,
        // a level less.
        private void BorrowLight(World world, SkyLight light, int x, int y, int z, out int level, out int torch)
        {
            level = 0;
            torch = 0;
            for (int face = 0; face < ChunkMesher.FaceCount; face++)
            {
                Vector3Int n = ChunkMesher.FaceNormals[face];
                int nx = x + n.x, ny = y + n.y, nz = z + n.z;
                if (Blocks.BlocksLight(world.GetBlock(nx, ny, nz)) || world.IsOpaque(nx, ny, nz)) continue;

                level = Mathf.Max(level, light.Get(nx, ny, nz) - 1);
                if (glow != null) torch = Mathf.Max(torch, glow.Get(nx, ny, nz) - 1);
            }
        }

        // The light of a sloping face (a ramp, a roof corner's slopes) of the shaped block at cell, whose
        // normal is given in the cell: the brightest of the cells it opens onto, the one above and those
        // on its low sides, as 0 to 1. Lit by the cell above only, a slope under a full block was black.
        private void SlopedFaceLight(World world, SkyLight light, Vector3Int cell, Vector3 normal, out float level, out float torchLevel)
        {
            int best = 0, bestGlow = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                float component = axis == 0 ? normal.x : axis == 1 ? normal.y : normal.z;
                if (Mathf.Abs(component) < 0.1f) continue;

                int step = component > 0f ? 1 : -1;
                int x = cell.x + (axis == 0 ? step : 0), y = cell.y + (axis == 1 ? step : 0), z = cell.z + (axis == 2 ? step : 0);
                if (world.IsOpaque(x, y, z)) continue;

                int cellLevel = light.Get(x, y, z);
                int cellGlow = glow != null ? glow.Get(x, y, z) : 0;
                if (cellLevel == 0 && Blocks.BlocksLight(world.GetBlock(x, y, z))) BorrowLight(world, light, x, y, z, out cellLevel, out cellGlow);

                best = Mathf.Max(best, cellLevel);
                bestGlow = Mathf.Max(bestGlow, cellGlow);
            }

            level = best / (float)SkyLight.Max;
            torchLevel = bestGlow / (float)Blocks.TorchLight;
        }

        // Reads the cells in front of a face and works out the light of its four corners.
        private void SampleFront(World world, SkyLight light, int x, int y, int z, int face)
        {
            Vector3Int axisA = ChunkMesher.FaceAxisA[face];
            Vector3Int axisB = ChunkMesher.FaceAxisB[face];
            for (int b = -1; b <= 1; b++)
            {
                for (int a = -1; a <= 1; a++)
                {
                    int cx = x + axisA.x * a + axisB.x * b;
                    int cy = y + axisA.y * a + axisB.y * b;
                    int cz = z + axisA.z * a + axisB.z * b;
                    int index = (a + 1) + 3 * (b + 1);
                    frontOpaque[index] = world.IsOpaque(cx, cy, cz);
                    frontLevel[index] = frontOpaque[index] ? 0 : light.Get(cx, cy, cz);
                    frontGlow[index] = frontOpaque[index] || glow == null ? 0 : glow.Get(cx, cy, cz);

                    // A shaped block that stops light (a slab, a wall, a slope) holds none: what shows of
                    // the faces beside it is lit by the cells around it instead, a level less.
                    if (!frontOpaque[index] && frontLevel[index] == 0 && Blocks.BlocksLight(world.GetBlock(cx, cy, cz)))
                    {
                        BorrowLight(world, light, cx, cy, cz, out frontLevel[index], out frontGlow[index]);
                    }
                }
            }

            Vector3[] corners = ChunkMesher.FaceCorners[face];
            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 position = corners[corner];
                int a = Vector3.Dot(position, axisA) > 0.5f ? 1 : -1;
                int b = Vector3.Dot(position, axisB) > 0.5f ? 1 : -1;

                int sideA = (a + 1) + 3;
                int sideB = 1 + 3 * (b + 1);
                int diagonal = (a + 1) + 3 * (b + 1);

                int sum = frontLevel[4];
                int glowSum = frontGlow[4];
                int count = 1;
                if (!frontOpaque[sideA])
                {
                    sum += frontLevel[sideA];
                    glowSum += frontGlow[sideA];
                    count++;
                }

                if (!frontOpaque[sideB])
                {
                    sum += frontLevel[sideB];
                    glowSum += frontGlow[sideB];
                    count++;
                }

                // Light does not pass between two opaque blocks that touch by an edge.
                if (!frontOpaque[diagonal] && !(frontOpaque[sideA] && frontOpaque[sideB]))
                {
                    sum += frontLevel[diagonal];
                    glowSum += frontGlow[diagonal];
                    count++;
                }

                cornerLight[corner] = sum / (count * (float)SkyLight.Max);
                cornerGlow[corner] = glowSum / (count * (float)Blocks.TorchLight);
            }
        }

        private float GlowAt(int x, int y, int z) => glow == null ? 0f : glow.Get(x, y, z) / (float)Blocks.TorchLight;

        // top: height of the block's top, 1 for a full block. The corners' light is in cornerLight
        // and cornerGlow.
        private void AddFace(Vector3 blockOrigin, int face, float top, List<int> triangles, bool turnToLight)
        {
            int first = vertices.Count;
            Vector3[] corners = ChunkMesher.FaceCorners[face];
            Vector3 normal = ChunkMesher.FaceNormals[face];

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 corner = corners[i];
                if (corner.y > 0f) corner.y = top;
                vertices.Add(blockOrigin + corner);
                normals.Add(normal);
                colours.Add(new Color32((byte)Mathf.RoundToInt(cornerLight[i] * 255f), (byte)Mathf.RoundToInt(cornerGlow[i] * 255f), 0, 255));
            }

            // The quad is split along the diagonal whose corners are brightest together, so that a
            // dark corner shades its own triangle instead of a band across the face.
            bool alongOtherDiagonal = turnToLight && cornerLight[0] + cornerLight[2] < cornerLight[1] + cornerLight[3];
            if (alongOtherDiagonal)
            {
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first + 3);
                triangles.Add(first + 1);
                triangles.Add(first + 3);
                triangles.Add(first);
            }
            else
            {
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first);
                triangles.Add(first + 2);
                triangles.Add(first + 3);
            }
        }

        // A shaped wood block (Shapes): its boxes, a slope's ramp, or a fence's post and rails toward
        // the fences, low walls, full blocks and walls beside it. Each face is lit by the cell it faces
        // (a shaped block that stops light holds none of its own), and one on its cell's side is left out
        // against an opaque block.
        private void AddShape(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell)
        {
            int facing = Shapes.FacingOf(block);
            switch (Shapes.ShapeOf(block))
            {
                case Shape.Post:
                    AddBox(world, light, block, origin, cell, new Vector3(0.375f, 0f, 0.375f), new Vector3(0.625f, 1f, 0.625f));
                    break;
                case Shape.Floor:
                    AddBox(world, light, block, origin, cell, Vector3.zero, new Vector3(1f, 0.5f, 1f));
                    break;
                case Shape.FlatRoof:
                    AddBox(world, light, block, origin, cell, new Vector3(0f, 0.5f, 0f), Vector3.one);
                    break;
                case Shape.Slope:
                    AddSlope(world, light, block, origin, cell, facing);
                    break;
                case Shape.Fence:
                    AddFence(world, light, block, origin, cell);
                    break;
                case Shape.OuterCorner:
                    AddOuterCorner(world, light, block, origin, cell, facing);
                    break;
                case Shape.InnerCorner:
                    AddInnerCorner(world, light, block, origin, cell, facing);
                    break;
                case Shape.Ladder:
                    AddLadder(world, light, block, origin, cell, facing);
                    break;
                default:
                    // Walls (an L at a corner), stairs (turning at corners), posts in a corner, beams, upright
                    // slabs, low walls and columns: their boxes, from Shapes.ConnectionsOf for what joins.
                    shapeBoxes.Clear();
                    Shapes.GetLocalBoxes(Shapes.ShapeOf(block), facing, Shapes.ConnectionsOf(world, new BlockPos(cell.x, cell.y, cell.z), block), shapeBoxes);
                    foreach ((System.Numerics.Vector3 min, System.Numerics.Vector3 max) in shapeBoxes) AddBox(world, light, block, origin, cell, min.ToUnity(), max.ToUnity());
                    break;
            }
        }

        private readonly List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)> shapeBoxes = new List<(System.Numerics.Vector3 min, System.Numerics.Vector3 max)>();

        // A box given facing +z (Shapes.Turned), turned with the block.
        private void AddTurnedBox(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, Vector3 min, Vector3 max, int facing)
        {
            (System.Numerics.Vector3 min, System.Numerics.Vector3 max) box = Shapes.Turned(min.ToNumerics(), max.ToNumerics(), facing);
            AddBox(world, light, block, origin, cell, box.min.ToUnity(), box.max.ToUnity());
        }

        // Two rails against the edge of the facing and four rungs between them, standing a little off it.
        private void AddLadder(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing)
        {
            float back = 1f - Shapes.LadderThickness;
            AddTurnedBox(world, light, block, origin, cell, new Vector3(0.1f, 0f, back), new Vector3(0.22f, 1f, 1f), facing);
            AddTurnedBox(world, light, block, origin, cell, new Vector3(0.78f, 0f, back), new Vector3(0.9f, 1f, 1f), facing);
            for (int rung = 0; rung < 4; rung++)
            {
                float y = 0.13f + rung * 0.25f;
                AddTurnedBox(world, light, block, origin, cell, new Vector3(0.22f, y, back + 0.025f), new Vector3(0.78f, y + 0.07f, 0.97f), facing);
            }
        }

        // A roof's outer corner given facing +z: a quarter of a pyramid, low along x 0 and z 0, its tip
        // at the corner (1, 1, 1); two sloping triangles, a triangle on each high side, the bottom.
        private void AddOuterCorner(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing)
        {
            AddShapeFace(world, light, block, origin, cell, facing, -1, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 1));
            AddShapeFace(world, light, block, origin, cell, facing, -1, new Vector3(0, 0, 0), new Vector3(1, 1, 1), new Vector3(0, 0, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 0, new Vector3(1, 0, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 4, new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 3, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1));
        }

        // A roof's inner corner given facing +z: a valley, high along x 1 and z 1, low only at (0, 0, 0);
        // two sloping triangles, the two high sides, a triangle on each low side, the bottom.
        private void AddInnerCorner(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing)
        {
            AddShapeFace(world, light, block, origin, cell, facing, -1, new Vector3(0, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1));
            AddShapeFace(world, light, block, origin, cell, facing, -1, new Vector3(0, 0, 0), new Vector3(1, 1, 1), new Vector3(0, 1, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 0, new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(1, 0, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 4, new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 1, new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 1));
            AddShapeFace(world, light, block, origin, cell, facing, 5, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0));
            AddShapeFace(world, light, block, origin, cell, facing, 3, new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1));
        }

        // A flat face of three or four corners given facing +z, turned with the block: face is the cell
        // side it lies on (culled against an opaque neighbour, lit by the cell beyond it) or -1 for a
        // sloping face (lit by the cells it opens onto, SlopedFaceLight). Its normal is worked out from its corners, which are
        // put in the order that faces it outward: toward its side, or upward for a sloping face.
        private void AddShapeFace(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing, int face, params Vector3[] given)
        {
            int turned = face < 0 ? ChunkMesher.TopFace : TurnFace(face, facing);
            Vector3Int toward = ChunkMesher.FaceNormals[turned];
            if (face >= 0 && HidesFace(world, cell.x + toward.x, cell.y + toward.y, cell.z + toward.z)) return;

            // Newell's normal of the turned corners (a triangle repeats its last corner), which points
            // the way cross(b - a, c - a) does, the front side here as for AddSlope's ramp; the order is
            // reversed when it points the wrong way.
            var points = new Vector3[4];
            for (int i = 0; i < 4; i++) points[i] = TurnPoint(given[Mathf.Min(i, given.Length - 1)], facing);
            Vector3 normal = Vector3.zero;
            for (int i = 0; i < 4; i++)
            {
                Vector3 a = points[i], b = points[(i + 1) % 4];
                normal += new Vector3((a.y - b.y) * (a.z + b.z), (a.z - b.z) * (a.x + b.x), (a.x - b.x) * (a.y + b.y));
            }

            normal = normal.normalized;
            if (Vector3.Dot(normal, face < 0 ? Vector3.up : (Vector3)toward) < 0f)
            {
                System.Array.Reverse(points);
                normal = -normal;
            }

            // A side face is lit by the cell beyond it, a sloping one by those it opens onto.
            float level, torchLevel;
            if (face < 0) SlopedFaceLight(world, light, cell, normal, out level, out torchLevel);
            else
            {
                SampleFront(world, light, cell.x + toward.x, cell.y + toward.y, cell.z + toward.z, turned);
                level = frontLevel[4] / (float)SkyLight.Max;
                torchLevel = frontGlow[4] / (float)Blocks.TorchLight;
            }

            for (int i = 0; i < 4; i++)
            {
                cornerLight[i] = level;
                cornerGlow[i] = torchLevel;
                corners[i] = points[i];
            }

            AddQuad(origin, corners, normal, false);
            AddLocalUvs(BlockAtlas.TileFor(block, turned), turned, corners, cell);
        }

        // A post a little thinner than a wall, and two rails toward each side it joins (Shapes.ConnectionsOf:
        // fences, low walls, full blocks and walls facing it beside it, or along its axis when alone).
        private void AddFence(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell)
        {
            AddBox(world, light, block, origin, cell, new Vector3(0.39f, 0f, 0.39f), new Vector3(0.61f, 1f, 0.61f));
            int connections = Shapes.ConnectionsOf(world, new BlockPos(cell.x, cell.y, cell.z), block);
            foreach ((int dx, int dz, int bit) in new[] { (0, 1, Shapes.TowardPlusZ), (0, -1, Shapes.TowardMinusZ), (1, 0, Shapes.TowardPlusX), (-1, 0, Shapes.TowardMinusX) })
            {
                if ((connections & bit) == 0) continue;

                foreach ((float bottom, float top) in new[] { (0.3f, 0.45f), (0.7f, 0.85f) })
                {
                    Vector3 min = dx > 0 ? new Vector3(0.61f, bottom, 0.45f) : dx < 0 ? new Vector3(0f, bottom, 0.45f) : dz > 0 ? new Vector3(0.45f, bottom, 0.61f) : new Vector3(0.45f, bottom, 0f);
                    Vector3 max = dx > 0 ? new Vector3(1f, top, 0.55f) : dx < 0 ? new Vector3(0.39f, top, 0.55f) : dz > 0 ? new Vector3(0.55f, top, 1f) : new Vector3(0.55f, top, 0.39f);
                    AddBox(world, light, block, origin, cell, min, max);
                }
            }
        }

        // A box within the block's cell (min and max from 0 to 1): six faces with the part of the tile
        // that lines up with the world, so that it carries on from the block beside it.
        private void AddBox(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, Vector3 min, Vector3 max)
        {
            for (int face = 0; face < ChunkMesher.FaceCount; face++)
            {
                Vector3Int normal = ChunkMesher.FaceNormals[face];
                bool onSide = face == 0 ? max.x >= 1f : face == 1 ? min.x <= 0f : face == 2 ? max.y >= 1f : face == 3 ? min.y <= 0f : face == 4 ? max.z >= 1f : min.z <= 0f;
                if (onSide && HidesFace(world, cell.x + normal.x, cell.y + normal.y, cell.z + normal.z)) continue;

                SampleFront(world, light, cell.x + normal.x, cell.y + normal.y, cell.z + normal.z, face);
                Vector3[] unit = ChunkMesher.FaceCorners[face];
                for (int i = 0; i < 4; i++)
                {
                    corners[i] = new Vector3(Mathf.Lerp(min.x, max.x, unit[i].x), Mathf.Lerp(min.y, max.y, unit[i].y), Mathf.Lerp(min.z, max.z, unit[i].z));
                }

                AddQuad(origin, corners, normal, true);
                AddLocalUvs(BlockAtlas.TileFor(block, face), face, corners, cell);
            }
        }

        // A slope rising toward its facing: the ramp, a triangle on each side, the back and the bottom.
        // Given facing +z, then turned as Shapes.Turned turns boxes.
        private void AddSlope(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing)
        {
            // Bottom (face 3), back (+z, face 4), the side at x 1 (face 0), the side at x 0 (face 1), the ramp.
            AddSlopeFace(world, light, block, origin, cell, facing, 3, new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) });
            AddSlopeFace(world, light, block, origin, cell, facing, 4, new[] { new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 1) });
            AddSlopeFace(world, light, block, origin, cell, facing, 0, new[] { new Vector3(1, 0, 0), new Vector3(1, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 0, 1) });
            AddSlopeFace(world, light, block, origin, cell, facing, 1, new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(0, 1, 1), new Vector3(0, 0, 0) });
            AddSlopeFace(world, light, block, origin, cell, facing, -1, new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 0, 0) });
        }

        // One face of a slope given facing +z (face: the cell side it lies on, or -1 for the ramp, lit by
        // the cells it opens onto): turned, culled against an opaque neighbour, lit evenly by the cell
        // in front of it.
        private void AddSlopeFace(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing, int face, Vector3[] given)
        {
            int turned = face < 0 ? ChunkMesher.TopFace : TurnFace(face, facing);
            Vector3Int toward = ChunkMesher.FaceNormals[turned];
            if (face >= 0 && HidesFace(world, cell.x + toward.x, cell.y + toward.y, cell.z + toward.z)) return;

            Vector3 normal = face < 0 ? TurnPoint(new Vector3(0.5f, 0.5f + 0.70710678f, 0.5f - 0.70710678f), facing) - new Vector3(0.5f, 0.5f, 0.5f) : (Vector3)toward;
            float level, torchLevel;
            if (face < 0) SlopedFaceLight(world, light, cell, normal, out level, out torchLevel);
            else
            {
                SampleFront(world, light, cell.x + toward.x, cell.y + toward.y, cell.z + toward.z, turned);
                level = frontLevel[4] / (float)SkyLight.Max;
                torchLevel = frontGlow[4] / (float)Blocks.TorchLight;
            }

            for (int i = 0; i < 4; i++)
            {
                cornerLight[i] = level;
                cornerGlow[i] = torchLevel;
                corners[i] = TurnPoint(given[i], facing);
            }
            AddQuad(origin, corners, normal, false);
            AddLocalUvs(BlockAtlas.TileFor(block, turned), turned, corners, cell);
        }

        // A point of the cell turned about its middle by quarter turns: (x, z) -> (z, 1 - x).
        internal static Vector3 TurnPoint(Vector3 point, int facing)
        {
            for (int turn = 0; turn < (facing & 3); turn++)
            {
                point = new Vector3(point.z, point.y, 1f - point.x);
            }

            return point;
        }

        // A side face turned with the block: +z becomes +x, +x becomes -z, and so on.
        private static int TurnFace(int face, int facing)
        {
            for (int turn = 0; turn < (facing & 3); turn++)
            {
                face = face == 4 ? 0 : face == 0 ? 5 : face == 5 ? 1 : face == 1 ? 4 : face;
            }

            return face;
        }

        private readonly Vector3[] corners = new Vector3[4];

        // Four corners (within the cell, from origin), lit by cornerLight and cornerGlow; split along
        // the brighter diagonal when asked, as AddFace does.
        private void AddQuad(Vector3 origin, Vector3[] quad, Vector3 normal, bool turnToLight)
        {
            int first = vertices.Count;
            for (int i = 0; i < 4; i++)
            {
                vertices.Add(origin + quad[i]);
                normals.Add(normal);
                colours.Add(new Color32((byte)Mathf.RoundToInt(cornerLight[i] * 255f), (byte)Mathf.RoundToInt(cornerGlow[i] * 255f), 0, 255));
            }

            bool alongOtherDiagonal = turnToLight && cornerLight[0] + cornerLight[2] < cornerLight[1] + cornerLight[3];
            int[] order = alongOtherDiagonal ? new[] { 1, 2, 3, 1, 3, 0 } : new[] { 0, 1, 2, 0, 2, 3 };
            foreach (int index in order)
            {
                solidTriangles.Add(first + index);
            }
        }

        // The part of a tile under each corner, as the face's axes run through the cell (the same as a
        // full face's corners, AddTileUvs, at the cell's corners), from the variant of the tile the cell
        // shows on that face (never turned: a shape's boxes line up with the world).
        private void AddLocalUvs(int tile, int face, Vector3[] quad, Vector3Int cell)
        {
            int slot = variants.Pick(tile, cell.x, cell.y, cell.z, face, out _, out _);
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = quad[i];
                float u, v;
                switch (face)
                {
                    case 0: u = p.z; v = p.y; break;
                    case 1: u = 1f - p.z; v = p.y; break;
                    case 2: u = p.x; v = p.z; break;
                    case 3: u = p.z; v = p.x; break;
                    case 4: u = 1f - p.x; v = p.y; break;
                    default: u = p.x; v = p.y; break;
                }

                u = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, Mathf.Clamp01(u));
                v = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, Mathf.Clamp01(v));
                uvs.Add(new Vector2((slot + u) / variants.SlotCount, v));
            }
        }

        // The overlay of this ore (its OreKind less one) on the face just added: a variant picked for that face,
        // turned and mirrored on every face if the overlay rotates. Its hash is told apart from the
        // rock's (the face counted past the six), so the two do not pick and turn alike.
        private void AddOverlayUvs(int ore, int x, int y, int z, int face)
        {
            while (overlayUvs.Count < vertices.Count - 4) overlayUvs.Add(Vector2.zero);

            int slot = overlays.Pick(ore, x, y, z, face + ChunkMesher.FaceCount, out int turn, out bool mirror, true);
            for (int i = 0; i < 4; i++)
            {
                Vector2 corner = ChunkMesher.CornerUvs[(i + turn) & 3];
                float u = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, mirror ? 1f - corner.x : corner.x);
                float v = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, corner.y);
                overlayUvs.Add(new Vector2((slot + u) / overlays.SlotCount, v));

                int vertex = vertices.Count - 4 + i;
                Color32 colour = colours[vertex];
                colour.b = 255;
                colours[vertex] = colour;
            }
        }

        // A whole face of the block at (x, y, z): the variant of its tile picked for that face
        // (TileVariants), turned by quarter turns and mirrored when the tile rotates there.
        private void AddTileUvs(int tile, int x, int y, int z, int face)
        {
            int slot = variants.Pick(tile, x, y, z, face, out int turn, out bool mirror);
            for (int i = 0; i < 4; i++)
            {
                Vector2 corner = ChunkMesher.CornerUvs[(i + turn) & 3];
                float u = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, mirror ? 1f - corner.x : corner.x);
                float v = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, corner.y);
                uvs.Add(new Vector2((slot + u) / variants.SlotCount, v));
            }
        }
    }
}
