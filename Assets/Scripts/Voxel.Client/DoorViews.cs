using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Voxel.Core;

namespace Voxel.Client
{
    // How doors look (Blocks.Door, which the chunk meshes leave out): a panel of planks two cells high
    // with three rails on its inner side, iron hinges and handles, across the far edge of its cells
    // closed, swung back against its hinge side open (Doors). Drawn instanced, wood then iron, for the
    // doors within ViewDistance of the camera. Where they stand is kept here: their lower halves, found
    // in the chunks players changed (doors are never generated), then followed through BlockChanged and
    // chunks replaced wholesale.
    internal sealed class DoorViews
    {
        private const float ViewDistance = 96f;
        private const int MaxInstancesPerCall = 1023;

        private static readonly Vector3 Hinge = new Vector3(Doors.Thickness * 0.5f, 0f, 1f - Doors.Thickness * 0.5f);

        private readonly Mesh door;
        private readonly RenderParams woodParams;
        private readonly RenderParams ironParams;
        private readonly HashSet<BlockPos> doors = new HashSet<BlockPos>();
        private readonly List<BlockPos> gone = new List<BlockPos>();
        private readonly List<Matrix4x4> placed = new List<Matrix4x4>();
        private Matrix4x4[] batch = new Matrix4x4[64];

        public DoorViews(Material wood, Material iron)
        {
            door = BoxMesh.Build("Door", new[] { WoodBoxes(), IronBoxes() });
            woodParams = new RenderParams(wood) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
            ironParams = new RenderParams(iron) { shadowCastingMode = ShadowCastingMode.On, receiveShadows = true };
        }

        // Facing +z, closed, its foot at the cell's corner: the panel across the far edge (z near 1).
        // No two boxes have faces in one plane.
        private static List<(Vector3 centre, Vector3 size)> WoodBoxes()
        {
            float back = 1f - Doors.Thickness;
            var boxes = new List<(Vector3, Vector3)> { (new Vector3(0.5f, 1f, 1f - Doors.Thickness * 0.5f), new Vector3(0.96f, 1.96f, Doors.Thickness)) };
            foreach (float y in new[] { 0.35f, 1.02f, 1.65f })
            {
                boxes.Add((new Vector3(0.5f, y, back - 0.02f), new Vector3(0.86f, 0.12f, 0.042f)));
            }

            return boxes;
        }

        private static List<(Vector3 centre, Vector3 size)> IronBoxes()
        {
            float back = 1f - Doors.Thickness;
            return new List<(Vector3, Vector3)>
            {
                (new Vector3(0.17f, 0.35f, back - 0.045f), new Vector3(0.26f, 0.06f, 0.03f)),
                (new Vector3(0.17f, 1.65f, back - 0.045f), new Vector3(0.26f, 0.06f, 0.03f)),
                (new Vector3(0.84f, 0.8f, back - 0.07f), new Vector3(0.05f, 0.13f, 0.05f)),
                (new Vector3(0.84f, 0.8f, 1.03f), new Vector3(0.051f, 0.131f, 0.051f))
            };
        }

        // A world shown: its doors, from the chunks players changed.
        public void Reset(World world)
        {
            doors.Clear();
            foreach (Chunk chunk in world.Chunks)
            {
                if (chunk.IsModified && !chunk.IsEmpty) AddDoors(chunk);
            }
        }

        public void Clear() => doors.Clear();

        // A chunk replaced wholesale (a client receiving it): its doors again.
        public void RefreshChunk(Chunk chunk)
        {
            gone.Clear();
            foreach (BlockPos pos in doors)
            {
                if (ChunkCoord.FromBlock(pos).Equals(chunk.Coord)) gone.Add(pos);
            }

            foreach (BlockPos pos in gone)
            {
                doors.Remove(pos);
            }

            AddDoors(chunk);
        }

        public void Changed(BlockPos pos, ushort current)
        {
            if (Blocks.IsDoor(current) && !Doors.IsUpper(current)) doors.Add(pos);
            else doors.Remove(pos);
        }

        private void AddDoors(Chunk chunk)
        {
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
                        if (Blocks.IsDoor(block) && !Doors.IsUpper(block)) doors.Add(new BlockPos(originX + x, originY + y, originZ + z));
                    }
                }
            }
        }

        public void Render(World world, Vector3 viewer)
        {
            if (doors.Count == 0) return;

            placed.Clear();
            var bounds = new Bounds(viewer, Vector3.zero);
            foreach (BlockPos pos in doors)
            {
                ushort block = world.GetBlock(pos);
                if (!Blocks.IsDoor(block)) continue;

                var cell = new Vector3(pos.X + 0.5f, pos.Y, pos.Z + 0.5f);
                if ((cell - viewer).sqrMagnitude > ViewDistance * ViewDistance) continue;

                placed.Add(Placement(pos, block));
                bounds.Encapsulate(cell);
            }

            if (placed.Count == 0) return;

            bounds.Expand(3f);
            Draw(woodParams, 0, bounds);
            Draw(ironParams, 1, bounds);
        }

        // Turned by its facing about its cells' middle, and open, swung a quarter turn about its hinge.
        private static Matrix4x4 Placement(BlockPos pos, ushort block)
        {
            var middle = new Vector3(0.5f, 0f, 0.5f);
            Matrix4x4 turn = Matrix4x4.Translate(new Vector3(pos.X, pos.Y, pos.Z) + middle)
                * Matrix4x4.Rotate(Quaternion.Euler(0f, 90f * Doors.FacingOf(block), 0f))
                * Matrix4x4.Translate(-middle);
            if (!Doors.IsOpen(block)) return turn;

            return turn * Matrix4x4.Translate(Hinge) * Matrix4x4.Rotate(Quaternion.Euler(0f, 90f, 0f)) * Matrix4x4.Translate(-Hinge);
        }

        private void Draw(RenderParams parameters, int submesh, Bounds bounds)
        {
            if (batch.Length < placed.Count) batch = new Matrix4x4[Mathf.NextPowerOfTwo(placed.Count)];
            placed.CopyTo(batch);
            parameters.worldBounds = bounds;
            for (int start = 0; start < placed.Count; start += MaxInstancesPerCall)
            {
                Graphics.RenderMeshInstanced(parameters, door, submesh, batch, Mathf.Min(MaxInstancesPerCall, placed.Count - start), start);
            }
        }
    }
}
