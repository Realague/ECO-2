using System.Collections.Generic;
using UnityEngine;
using Voxel.Core;

namespace Voxel.Client
{
    // Crée le monde, l'affiche et remaille les chunks touchés par un changement de bloc.
    public class VoxelWorld : MonoBehaviour
    {
        private static readonly BlockPos[] NeighbourOffsets =
        {
            new BlockPos(1, 0, 0), new BlockPos(-1, 0, 0),
            new BlockPos(0, 1, 0), new BlockPos(0, -1, 0),
            new BlockPos(0, 0, 1), new BlockPos(0, 0, -1)
        };

        [SerializeField] private Vector3Int sizeInChunks = new Vector3Int(8, 4, 8);
        [SerializeField] private int seed = 1;
        [SerializeField] private Material blockMaterial;

        private readonly Dictionary<Chunk, Mesh> meshes = new Dictionary<Chunk, Mesh>();
        private readonly HashSet<Chunk> dirtyChunks = new HashSet<Chunk>();
        private Material materialInstance;
        private Texture2D atlas;

        public World World { get; private set; }

        private void Awake()
        {
            World = new World(sizeInChunks.x, sizeInChunks.y, sizeInChunks.z);
            World.Generate(new ProvisionalHillsGenerator(), seed);
            World.BlockChanged += OnBlockChanged;

            atlas = BlockAtlas.CreateTexture();
            materialInstance = new Material(blockMaterial) { mainTexture = atlas };

            foreach (Chunk chunk in World.Chunks)
            {
                CreateChunkObject(chunk);
            }
        }

        private void LateUpdate()
        {
            if (dirtyChunks.Count == 0) return;

            foreach (Chunk chunk in dirtyChunks)
            {
                ChunkMesher.Build(World, chunk, meshes[chunk]);
            }

            dirtyChunks.Clear();
        }

        private void OnDestroy()
        {
            if (World != null) World.BlockChanged -= OnBlockChanged;

            foreach (Mesh mesh in meshes.Values)
            {
                Destroy(mesh);
            }

            Destroy(materialInstance);
            Destroy(atlas);
        }

        private void CreateChunkObject(Chunk chunk)
        {
            var mesh = new Mesh { name = $"Chunk {chunk.Coord}" };
            ChunkMesher.Build(World, chunk, mesh);
            meshes.Add(chunk, mesh);

            var chunkObject = new GameObject(mesh.name);
            chunkObject.transform.SetParent(transform, false);
            chunkObject.transform.localPosition = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * Chunk.Size;
            chunkObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            chunkObject.AddComponent<MeshRenderer>().sharedMaterial = materialInstance;
        }

        private void OnBlockChanged(BlockPos pos, byte previous, byte current)
        {
            MarkDirty(pos);

            // Un bloc en bord de chunk change aussi les faces visibles du chunk voisin.
            foreach (BlockPos offset in NeighbourOffsets)
            {
                MarkDirty(pos + offset);
            }
        }

        private void MarkDirty(BlockPos pos)
        {
            Chunk chunk = World.GetChunk(ChunkCoord.FromBlock(pos));
            if (chunk != null) dirtyChunks.Add(chunk);
        }
    }
}
