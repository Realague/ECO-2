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
    //
    // The work is done by a ChunkMeshBuilder, which only reads the world and the light: builders run
    // on worker threads (ChunkMeshing), each with its own buffers, and only ApplyTo touches the Mesh.
    internal static class ChunkMesher
    {
        public const int TopFace = 2;
        public const int BottomFace = 3;

        public const int SolidSubmesh = 0;
        public const int WaterSubmesh = 1;

        public const int FaceCount = 6;
        public const float UvInset = 0.01f;
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
        private readonly List<Color32> colours = new List<Color32>();
        private readonly List<int> solidTriangles = new List<int>();
        private readonly List<int> waterTriangles = new List<int>();

        // The cells in front of a face, 3 × 3 around the one it touches, indexed (a + 1) + 3 * (b + 1)
        // along the face's two axes.
        private readonly bool[] frontOpaque = new bool[9];
        private readonly int[] frontLevel = new int[9];
        private readonly int[] frontGlow = new int[9];
        private readonly float[] cornerLight = new float[4];
        private readonly float[] cornerGlow = new float[4];

        // The light of torches for the chunk being built; null for none.
        private BlockLight glow;

        // True when the last chunk built has no face to draw.
        public bool IsEmpty => vertices.Count == 0;

        public void Build(World world, SkyLight light, Chunk chunk, BlockLight blockLight = null)
        {
            glow = blockLight;
            vertices.Clear();
            normals.Clear();
            uvs.Clear();
            colours.Clear();
            solidTriangles.Clear();
            waterTriangles.Clear();
            if (chunk.IsEmpty) return;

            int originX = chunk.Coord.X * Chunk.Size;
            int originY = chunk.Coord.Y * Chunk.Size;
            int originZ = chunk.Coord.Z * Chunk.Size;

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

                        for (int face = 0; face < ChunkMesher.FaceCount; face++)
                        {
                            Vector3Int normal = ChunkMesher.FaceNormals[face];
                            if (world.IsOpaque(worldX + normal.x, worldY + normal.y, worldZ + normal.z)) continue;

                            SampleFront(world, light, worldX + normal.x, worldY + normal.y, worldZ + normal.z, face);
                            AddFace(new Vector3(x, y, z), face, 1f, solidTriangles, true);
                            AddTileUvs(BlockAtlas.TileFor(block, face));
                        }
                    }
                }
            }
        }

        public void ApplyTo(Mesh mesh)
        {
            mesh.Clear();
            mesh.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colours);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(solidTriangles, ChunkMesher.SolidSubmesh);
            mesh.SetTriangles(waterTriangles, ChunkMesher.WaterSubmesh);
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

        // A shaped wood block (Shapes): its boxes, a slope's ramp, or a fence's post and rails toward the
        // fences beside it. Each face is lit by the cell it faces (a shaped block that stops light holds
        // none of its own), and one on its cell's side is left out against an opaque block.
        private void AddShape(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell)
        {
            int facing = Shapes.FacingOf(block);
            switch (Shapes.ShapeOf(block))
            {
                case Shape.Wall:
                    (System.Numerics.Vector3 min, System.Numerics.Vector3 max) wall = Shapes.Turned(new System.Numerics.Vector3(0f, 0f, 1f - Shapes.WallThickness), System.Numerics.Vector3.One, facing);
                    AddBox(world, light, block, origin, cell, wall.min.ToUnity(), wall.max.ToUnity());
                    break;
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
                case Shape.Stairs:
                    AddBox(world, light, block, origin, cell, Vector3.zero, new Vector3(1f, 0.5f, 1f));
                    AddTurnedBox(world, light, block, origin, cell, new Vector3(0f, 0.5f, 0.5f), Vector3.one, facing);
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
            }
        }

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
        // sloping face (lit by the cell above). Its normal is worked out from its corners, which are
        // put in the order that faces it outward: toward its side, or upward for a sloping face.
        private void AddShapeFace(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing, int face, params Vector3[] given)
        {
            int turned = face < 0 ? ChunkMesher.TopFace : TurnFace(face, facing);
            Vector3Int toward = ChunkMesher.FaceNormals[turned];
            if (face >= 0 && world.IsOpaque(cell.x + toward.x, cell.y + toward.y, cell.z + toward.z)) return;

            SampleFront(world, light, cell.x + toward.x, cell.y + toward.y, cell.z + toward.z, turned);
            float level = frontLevel[4] / (float)SkyLight.Max;
            float torchLevel = frontGlow[4] / (float)Blocks.TorchLight;

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

            for (int i = 0; i < 4; i++)
            {
                cornerLight[i] = level;
                cornerGlow[i] = torchLevel;
                corners[i] = points[i];
            }

            AddQuad(origin, corners, normal, false);
            AddLocalUvs(BlockAtlas.TileFor(block, turned), turned, corners);
        }

        // A post a little thinner than a wall, and two rails toward each fence beside it.
        private void AddFence(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell)
        {
            AddBox(world, light, block, origin, cell, new Vector3(0.39f, 0f, 0.39f), new Vector3(0.61f, 1f, 0.61f));
            var pos = new BlockPos(cell.x, cell.y, cell.z);
            foreach ((int dx, int dz) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                if (!Shapes.ConnectsToward(world, pos, dx, dz)) continue;

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
                if (onSide && world.IsOpaque(cell.x + normal.x, cell.y + normal.y, cell.z + normal.z)) continue;

                SampleFront(world, light, cell.x + normal.x, cell.y + normal.y, cell.z + normal.z, face);
                Vector3[] unit = ChunkMesher.FaceCorners[face];
                for (int i = 0; i < 4; i++)
                {
                    corners[i] = new Vector3(Mathf.Lerp(min.x, max.x, unit[i].x), Mathf.Lerp(min.y, max.y, unit[i].y), Mathf.Lerp(min.z, max.z, unit[i].z));
                }

                AddQuad(origin, corners, normal, true);
                AddLocalUvs(BlockAtlas.TileFor(block, face), face, corners);
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

        // One face of a slope given facing +z (face: the cell side it lies on, or -1 for the ramp, which
        // is lit by the cell above): turned, culled against an opaque neighbour, lit evenly by the cell
        // in front of it.
        private void AddSlopeFace(World world, SkyLight light, ushort block, Vector3 origin, Vector3Int cell, int facing, int face, Vector3[] given)
        {
            int turned = face < 0 ? ChunkMesher.TopFace : TurnFace(face, facing);
            Vector3Int toward = ChunkMesher.FaceNormals[turned];
            if (face >= 0 && world.IsOpaque(cell.x + toward.x, cell.y + toward.y, cell.z + toward.z)) return;

            SampleFront(world, light, cell.x + toward.x, cell.y + toward.y, cell.z + toward.z, turned);
            float level = frontLevel[4] / (float)SkyLight.Max;
            float torchLevel = frontGlow[4] / (float)Blocks.TorchLight;
            for (int i = 0; i < 4; i++)
            {
                cornerLight[i] = level;
                cornerGlow[i] = torchLevel;
                corners[i] = TurnPoint(given[i], facing);
            }

            Vector3 normal = face < 0 ? TurnPoint(new Vector3(0.5f, 0.5f + 0.70710678f, 0.5f - 0.70710678f), facing) - new Vector3(0.5f, 0.5f, 0.5f) : (Vector3)toward;
            AddQuad(origin, corners, normal, false);
            AddLocalUvs(BlockAtlas.TileFor(block, turned), turned, corners);
        }

        // A point of the cell turned about its middle by quarter turns: (x, z) -> (z, 1 - x).
        private static Vector3 TurnPoint(Vector3 point, int facing)
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
        // full face's corners, AddTileUvs, at the cell's corners).
        private void AddLocalUvs(int tile, int face, Vector3[] quad)
        {
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
                uvs.Add(new Vector2((tile + u) / BlockAtlas.TileCount, v));
            }
        }

        private void AddTileUvs(int tile)
        {
            for (int i = 0; i < 4; i++)
            {
                float u = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, ChunkMesher.CornerUvs[i].x);
                float v = Mathf.Lerp(ChunkMesher.UvInset, 1f - ChunkMesher.UvInset, ChunkMesher.CornerUvs[i].y);
                uvs.Add(new Vector2((tile + u) / BlockAtlas.TileCount, v));
            }
        }
    }
}
