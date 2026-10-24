using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Voxel.Core;

namespace Voxel.Client
{
    // Meshes many chunks at once: they are built in batches on every core (ChunkMeshBuilder.Build
    // only reads the world and the light) and each batch is handed, in order, to apply on the
    // calling thread, the main thread, the only one allowed to touch meshes and objects; the next
    // batch is built meanwhile. The caller blocks until all are done, so nothing changes the world
    // or the light meanwhile: every change (World.SetBlock, SkyLight.Update, BlockLight.Update,
    // chunks received from the server) runs on the main thread, and apply must not make any.
    internal static class ChunkMeshing
    {
        private static readonly int BatchSize = Math.Max(1, Environment.ProcessorCount) * 8;

        // Two sets of builders, reused from one call to the next (only the main thread calls
        // BuildAll): one batch is applied from one set while the next is built into the other.
        private static readonly ChunkMeshBuilder[][] Builders = { NewBuilders(), NewBuilders() };

        public static void BuildAll(World world, SkyLight light, IReadOnlyList<Chunk> chunks, Action<Chunk, ChunkMeshBuilder> apply, BlockLight blockLight = null)
        {
            if (chunks.Count == 0) return;

            Task building = BuildBatch(world, light, blockLight, chunks, 0, Builders[0]);
            try
            {
                for (int start = 0, set = 0; start < chunks.Count; start += BatchSize, set ^= 1)
                {
                    building.Wait();
                    int next = start + BatchSize;
                    building = next < chunks.Count ? BuildBatch(world, light, blockLight, chunks, next, Builders[set ^ 1]) : null;

                    ChunkMeshBuilder[] built = Builders[set];
                    int count = Math.Min(BatchSize, chunks.Count - start);
                    for (int i = 0; i < count; i++)
                    {
                        apply(chunks[start + i], built[i]);
                    }
                }
            }
            finally
            {
                // If apply failed, the batch being built must be finished before anything may
                // change the world again.
                if (building != null) ((IAsyncResult)building).AsyncWaitHandle.WaitOne();
            }
        }

        private static Task BuildBatch(World world, SkyLight light, BlockLight blockLight, IReadOnlyList<Chunk> chunks, int start, ChunkMeshBuilder[] builders)
        {
            int count = Math.Min(BatchSize, chunks.Count - start);
            return Task.Run(() => Parallel.For(0, count, i => builders[i].Build(world, light, chunks[start + i], blockLight)));
        }

        private static ChunkMeshBuilder[] NewBuilders()
        {
            var builders = new ChunkMeshBuilder[BatchSize];
            for (int i = 0; i < builders.Length; i++)
            {
                builders[i] = new ChunkMeshBuilder();
            }

            return builders;
        }
    }
}
