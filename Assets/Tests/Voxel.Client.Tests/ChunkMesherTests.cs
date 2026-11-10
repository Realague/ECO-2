using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client.Tests
{
    public class ChunkMesherTests
    {
        // A hash of every chunk mesh of a generated world, recorded when meshing moved to worker threads
        // (after checking that every chunk mesh of a 1024 world was unchanged by that move): the meshes
        // must stay exactly what they are.
        // It changes with what the terrain generator produces (TerrainGenerator.Version, like the
        // terrain fingerprints) or with the mesher's output (faces, light, atlas tiles): then copy the
        // new value from the failure message, saying why. Last changed when the wood blocks' 57 tiles
        // made the atlas 81 tiles wide (every texture coordinate moved), then when ores kept their host
        // rock (Ores): their faces show the rock's tile, with their overlay's coordinates, now hashed too.
        private const ulong ExpectedWorldMeshes = 0x8891A072371C7069UL;

        // The meshes without texture variants (TileVariants), for the tiles and the ores' overlays: the
        // pinned hash must not depend on which variant files are in Resources/Blocks, nor on whether the
        // atlas was made in this session.
        [SetUp]
        public void WithoutVariants()
        {
            BlockAtlas.Variants = TileVariants.None;
            BlockAtlas.Overlays = TileVariants.Single(BlockAtlas.OverlayNames);
        }

        // A rock holding an ore shows its own tile, the ore's overlay on the fourth texture coordinates
        // (its place in the overlays' row) and the blue of its vertex colour full (the shader's flag);
        // the other blocks have neither.
        [Test]
        public void AnOre_ShowsItsRock_WithItsOverlay()
        {
            var world = new World(1, 1, 1);
            world.SetBlock(4, 4, 4, Ores.Make(Blocks.Sandstone, OreKind.Iron));
            world.SetBlock(10, 4, 4, Blocks.Granite);
            var builder = new ChunkMeshBuilder();
            var mesh = new Mesh();
            try
            {
                builder.Build(world, new SkyLight(world), world.Chunks[0]);
                builder.ApplyTo(mesh);
                var uvs = new List<Vector2>();
                var overlay = new List<Vector2>();
                mesh.GetVertices(Vectors);
                mesh.GetUVs(0, uvs);
                mesh.GetUVs(ChunkMesher.OverlayUvChannel, overlay);
                mesh.GetColors(Colours);
                Assert.AreEqual(Vectors.Count, overlay.Count, "a coordinate for every vertex");

                int ironPlace = (int)OreKind.Iron - 1;
                int slots = BlockAtlas.Overlays.SlotCount;
                for (int i = 0; i < Vectors.Count; i++)
                {
                    bool ore = Vectors[i].x < 7f;
                    int tile = Mathf.FloorToInt(uvs[i].x * BlockAtlas.SlotCount);
                    Assert.AreEqual(ore ? BlockAtlas.SandstoneTile : BlockAtlas.GraniteTile, tile, $"vertex {i}");
                    Assert.AreEqual(ore ? 255 : 0, Colours[i].b, $"vertex {i}: the overlay's flag");
                    if (!ore)
                    {
                        Assert.AreEqual(Vector2.zero, overlay[i], $"vertex {i}");
                        continue;
                    }

                    Assert.That(overlay[i].x, Is.InRange((float)ironPlace / slots, (ironPlace + 1f) / slots), $"vertex {i}");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TheReferenceWorld_GivesTheRecordedMeshes()
        {
            World world = Generate();
            var light = new SkyLight(world);
            var builder = new ChunkMeshBuilder();
            var mesh = new Mesh();
            try
            {
                ulong hash = Offset;
                foreach (Chunk chunk in world.Chunks)
                {
                    builder.Build(world, light, chunk);
                    builder.ApplyTo(mesh);
                    hash = Mix(hash, HashOf(mesh));
                }

                Assert.AreEqual(ExpectedWorldMeshes, hash, $"Actual: 0x{hash:X16}UL");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void MeshingOnWorkerThreads_GivesTheSameMeshesAsOnTheMainThread()
        {
            World world = Generate();
            var light = new SkyLight(world);
            var mesh = new Mesh();
            try
            {
                var serial = new Dictionary<Chunk, ulong>();
                var builder = new ChunkMeshBuilder();
                foreach (Chunk chunk in world.Chunks)
                {
                    builder.Build(world, light, chunk);
                    builder.ApplyTo(mesh);
                    serial[chunk] = HashOf(mesh);
                }

                int faces = 0;
                var applied = new List<Chunk>();
                ChunkMeshing.BuildAll(world, light, world.Chunks, (chunk, built) =>
                {
                    built.ApplyTo(mesh);
                    Assert.AreEqual(serial[chunk], HashOf(mesh), $"Chunk {chunk.Coord}");
                    if (!built.IsEmpty) faces++;
                    applied.Add(chunk);
                });

                CollectionAssert.AreEqual(world.Chunks, applied, "every chunk, in order");
                Assert.That(faces, Is.GreaterThan(0).And.LessThan(world.Chunks.Count), "some chunks are air or buried rock");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        // Smoothed ground is meshed on worker threads as on the main thread, has its own submesh, and
        // shows the ground in it rather than in blocks.
        [Test]
        public void SmoothMeshing_OnWorkerThreads_GivesTheSameMeshesAsOnTheMainThread()
        {
            World world = Generate();
            var light = new SkyLight(world);
            var mesh = new Mesh();
            try
            {
                var serial = new Dictionary<Chunk, ulong>();
                var builder = new ChunkMeshBuilder();
                int smoothTriangles = 0;
                foreach (Chunk chunk in world.Chunks)
                {
                    builder.Build(world, light, chunk, null, true);
                    builder.ApplyTo(mesh);
                    serial[chunk] = HashOf(mesh);
                    Assert.AreEqual(3, mesh.subMeshCount, $"Chunk {chunk.Coord}");
                    smoothTriangles += (int)mesh.GetIndexCount(ChunkMesher.SmoothSubmesh);
                }

                Assert.Greater(smoothTriangles, 0, "the ground is smoothed");
                ChunkMeshing.BuildAll(world, light, world.Chunks, (chunk, built) =>
                {
                    built.ApplyTo(mesh);
                    Assert.AreEqual(serial[chunk], HashOf(mesh), $"Chunk {chunk.Coord}");
                }, null, true);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        // The smoothed ground has no hole, within chunks or between them, nor where it meets the ground
        // kept in blocks under planks laid on flat grass: every edge of the triangles of the ground and
        // the blocks is walked once each way (two faces meeting there, or four where two blocks only touch
        // by an edge), but along the world's sides, where the ground is cut off.
        [Test]
        public void SmoothGround_IsWatertight()
        {
            World world = Generate();
            int laid = 0;
            for (int z = 8; z < world.SizeZ - 8; z += 5)
            {
                for (int x = 8; x < world.SizeX - 8; x += 5)
                {
                    int top = world.GetTopHeight(x, z);
                    if (Blocks.TypeOf(world.GetBlock(x, top - 1, z)) != Blocks.Grass) continue;

                    bool flat = true;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            flat &= world.GetTopHeight(x + dx, z + dz) == top && world.GetSurfaceHeight(x + dx, z + dz) == top;
                        }
                    }

                    if (!flat) continue;

                    world.SetBlock(x, top, z, Blocks.FirstPlanks);
                    laid++;
                }
            }

            Assert.Greater(laid, 5, "planks laid on flat grass");
            var light = new SkyLight(world);
            var builder = new ChunkMeshBuilder();
            var mesh = new Mesh();
            var edges = new Dictionary<(long, long), int>();
            try
            {
                foreach (Chunk chunk in world.Chunks)
                {
                    builder.Build(world, light, chunk, null, true);
                    builder.ApplyTo(mesh);
                    mesh.GetVertices(Vectors);
                    var origin = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * Chunk.Size;
                    foreach (int submesh in new[] { ChunkMesher.SolidSubmesh, ChunkMesher.SmoothSubmesh })
                    {
                        mesh.GetTriangles(Triangles, submesh);
                        for (int t = 0; t < Triangles.Count; t += 3)
                        {
                            for (int corner = 0; corner < 3; corner++)
                            {
                                long from = KeyOf(Vectors[Triangles[t + corner]] + origin);
                                long to = KeyOf(Vectors[Triangles[t + (corner + 1) % 3]] + origin);
                                edges.TryGetValue((from, to), out int count);
                                edges[(from, to)] = count + 1;
                            }
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }

            Assert.Greater(edges.Count, 1000);
            int open = 0;
            foreach (KeyValuePair<(long, long), int> edge in edges)
            {
                edges.TryGetValue((edge.Key.Item2, edge.Key.Item1), out int back);
                if (back == edge.Value) continue;
                if (OnTheSides(edge.Key.Item1, world) && OnTheSides(edge.Key.Item2, world)) continue;

                open++;
            }

            Assert.AreEqual(0, open, "edges walked one way more than the other");
        }

        // A position to a thousandth of a block, as one number.
        private static long KeyOf(Vector3 p)
        {
            long x = Mathf.RoundToInt(p.x * 1000f), y = Mathf.RoundToInt(p.y * 1000f), z = Mathf.RoundToInt(p.z * 1000f);
            return (x + 1000) * 4_000_000_000_000L + (y + 1000) * 2_000_000L + (z + 1000);
        }

        private static bool OnTheSides(long key, World world)
        {
            long x = key / 4_000_000_000_000L - 1000;
            long z = key % 2_000_000L - 1000;
            return x <= 0 || z <= 0 || x >= world.SizeX * 1000L || z >= world.SizeZ * 1000L;
        }

        private static World Generate()
        {
            var world = new World(8, 4, 8);
            world.Generate(new TerrainGenerator(), 1);
            return world;
        }

        private const ulong Offset = 14695981039346656037UL;

        private static readonly List<Vector3> Vectors = new List<Vector3>();
        private static readonly List<Vector2> Uvs = new List<Vector2>();
        private static readonly List<Color32> Colours = new List<Color32>();
        private static readonly List<int> Triangles = new List<int>();

        private static ulong HashOf(Mesh mesh)
        {
            ulong hash = Offset;
            hash = Mix(hash, (ulong)mesh.vertexCount);
            hash = Mix(hash, (ulong)mesh.indexFormat);
            hash = Mix(hash, (ulong)mesh.subMeshCount);

            mesh.GetVertices(Vectors);
            foreach (Vector3 v in Vectors) hash = Mix(Mix(Mix(hash, Bits(v.x)), Bits(v.y)), Bits(v.z));
            mesh.GetNormals(Vectors);
            foreach (Vector3 v in Vectors) hash = Mix(Mix(Mix(hash, Bits(v.x)), Bits(v.y)), Bits(v.z));
            mesh.GetUVs(0, Uvs);
            foreach (Vector2 v in Uvs) hash = Mix(Mix(hash, Bits(v.x)), Bits(v.y));
            mesh.GetUVs(ChunkMesher.OverlayUvChannel, Uvs);
            foreach (Vector2 v in Uvs) hash = Mix(Mix(hash, Bits(v.x)), Bits(v.y));
            mesh.GetColors(Colours);
            foreach (Color32 c in Colours) hash = Mix(hash, (ulong)(c.r | c.g << 8 | c.b << 16 | c.a << 24));
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                mesh.GetTriangles(Triangles, submesh);
                hash = Mix(hash, (ulong)Triangles.Count);
                foreach (int index in Triangles) hash = Mix(hash, (ulong)index);
            }

            return hash;
        }

        private static ulong Bits(float value) => (uint)BitConverter.SingleToInt32Bits(value);

        private static ulong Mix(ulong hash, ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                hash = (hash ^ (byte)(value >> (8 * i))) * 1099511628211UL;
            }

            return hash;
        }
    }
}
