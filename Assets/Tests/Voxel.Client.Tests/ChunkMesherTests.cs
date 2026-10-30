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
        // made the atlas 81 tiles wide (every texture coordinate moved).
        private const ulong ExpectedWorldMeshes = 0xA5A60C27902AE740UL;

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
