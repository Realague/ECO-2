using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Voxel.Core
{
    // Why a plot could not be claimed or released.
    public enum ClaimRefusal : byte
    {
        None,
        Taken,
        QuotaReached,
        OutsideWorld,
        NotYours,
        NoName
    }

    // Land is claimed by plots: squares of PlotSize columns, the whole height of the world, on a
    // fixed grid from the world's corner. A player holds at most MaxPlotsPerPlayer of them.
    public static class ClaimRules
    {
        public const int PlotSize = 8;
        public const int MaxPlotsPerPlayer = 16;

        // The plot holding a column, along one axis (floor division, so it is right below 0 too).
        public static int PlotOf(int column) => column >= 0 ? column / PlotSize : (column + 1) / PlotSize - 1;

        // How many plots cover a world of this many columns along one axis.
        public static int PlotsAcross(int columns) => (columns + PlotSize - 1) / PlotSize;
    }

    // Someone who claims land: their plots and whom they let act on all of them.
    public sealed class ClaimOwner
    {
        public readonly int Index;
        public readonly string PlayerId;
        public readonly HashSet<string> Allowed = new HashSet<string>();

        public ClaimOwner(int index, string playerId)
        {
            Index = index;
            PlayerId = playerId;
        }

        public int PlotCount { get; internal set; }
    }

    // The claimed land of a world, kept by the server and saved with it (claims.dat): for each plot
    // the owner holding it, if any. On land claimed by someone, only they and the players they allow
    // may act (CanAct); free land is anyone's.
    public sealed class Claims
    {
        private const uint Magic = 0x4D414C43; // "CLAM"
        private const int FormatVersion = 1;
        private const int Free = -1;

        private readonly int[] plots;
        private readonly List<ClaimOwner> owners = new List<ClaimOwner>();
        private readonly Dictionary<string, ClaimOwner> ownersById = new Dictionary<string, ClaimOwner>();

        // Each plot's zone (ZoneOf), worked out again after the plots changed hands.
        private int[] zones;
        private bool zonesStale = true;

        public readonly int PlotsX;
        public readonly int PlotsZ;

        public Claims(int plotsX, int plotsZ)
        {
            PlotsX = plotsX;
            PlotsZ = plotsZ;
            plots = new int[plotsX * plotsZ];
            for (int i = 0; i < plots.Length; i++)
            {
                plots[i] = Free;
            }
        }

        // The plots of a world of this many columns.
        public static Claims ForWorld(int columnsX, int columnsZ) => new Claims(ClaimRules.PlotsAcross(columnsX), ClaimRules.PlotsAcross(columnsZ));

        public IReadOnlyList<ClaimOwner> Owners => owners;

        public bool IsInside(int plotX, int plotZ) => plotX >= 0 && plotZ >= 0 && plotX < PlotsX && plotZ < PlotsZ;

        // The owner of a plot, or null if it is free (or outside the world).
        public ClaimOwner OwnerOfPlot(int plotX, int plotZ)
        {
            if (!IsInside(plotX, plotZ)) return null;

            int owner = plots[plotZ * PlotsX + plotX];
            return owner == Free ? null : owners[owner];
        }

        // The owner of the plot holding a column, or null.
        public ClaimOwner OwnerAt(int x, int z) => OwnerOfPlot(ClaimRules.PlotOf(x), ClaimRules.PlotOf(z));

        public bool TryGetOwner(string playerId, out ClaimOwner owner)
        {
            owner = null;
            return !string.IsNullOrEmpty(playerId) && ownersById.TryGetValue(playerId, out owner);
        }

        public int CountOf(string playerId) => TryGetOwner(playerId, out ClaimOwner owner) ? owner.PlotCount : 0;

        // Whether a player may act on a column: free land, their own, or land whose owner allows them.
        public bool CanAct(string playerId, int x, int z)
        {
            ClaimOwner owner = OwnerAt(x, z);
            if (owner == null) return true;

            return !string.IsNullOrEmpty(playerId) && (owner.PlayerId == playerId || owner.Allowed.Contains(playerId));
        }

        public bool TryClaim(string playerId, int plotX, int plotZ, out ClaimRefusal refusal)
        {
            refusal = ClaimRefusal.None;
            if (string.IsNullOrEmpty(playerId)) refusal = ClaimRefusal.NoName;
            else if (!IsInside(plotX, plotZ)) refusal = ClaimRefusal.OutsideWorld;
            else if (plots[plotZ * PlotsX + plotX] != Free) refusal = ClaimRefusal.Taken;
            else if (CountOf(playerId) >= ClaimRules.MaxPlotsPerPlayer) refusal = ClaimRefusal.QuotaReached;
            if (refusal != ClaimRefusal.None) return false;

            ClaimOwner owner = OwnerFor(playerId);
            plots[plotZ * PlotsX + plotX] = owner.Index;
            owner.PlotCount++;
            zonesStale = true;
            return true;
        }

        public bool TryRelease(string playerId, int plotX, int plotZ, out ClaimRefusal refusal)
        {
            ClaimOwner owner = OwnerOfPlot(plotX, plotZ);
            refusal = !IsInside(plotX, plotZ) ? ClaimRefusal.OutsideWorld
                : owner == null || owner.PlayerId != playerId ? ClaimRefusal.NotYours
                : ClaimRefusal.None;
            if (refusal != ClaimRefusal.None) return false;

            plots[plotZ * PlotsX + plotX] = Free;
            owner.PlotCount--;
            zonesStale = true;
            return true;
        }

        // The zone of a plot: the plots of one owner joined to it by their sides, named by the
        // smallest plot index among them; -1 for a free plot or outside the world. Storage within one
        // zone shares what it holds (StoragePool).
        public int ZoneOf(int plotX, int plotZ)
        {
            if (!IsInside(plotX, plotZ)) return Free;

            if (zonesStale) FindZones();
            return zones[plotZ * PlotsX + plotX];
        }

        // Fills each plot's zone, joined plots of one owner by their sides. Plots are visited in index
        // order, so a zone's first plot is its smallest index.
        private void FindZones()
        {
            if (zones == null) zones = new int[plots.Length];
            for (int i = 0; i < zones.Length; i++)
            {
                zones[i] = int.MinValue;
            }

            var pending = new Stack<int>();
            for (int start = 0; start < plots.Length; start++)
            {
                if (zones[start] != int.MinValue) continue;

                int owner = plots[start];
                if (owner == Free)
                {
                    zones[start] = Free;
                    continue;
                }

                zones[start] = start;
                pending.Push(start);
                while (pending.Count > 0)
                {
                    int plot = pending.Pop();
                    int x = plot % PlotsX;
                    int z = plot / PlotsX;
                    Join(x - 1, z, owner, start, pending);
                    Join(x + 1, z, owner, start, pending);
                    Join(x, z - 1, owner, start, pending);
                    Join(x, z + 1, owner, start, pending);
                }
            }

            zonesStale = false;
        }

        private void Join(int x, int z, int owner, int zone, Stack<int> pending)
        {
            if (!IsInside(x, z)) return;

            int plot = z * PlotsX + x;
            if (zones[plot] != int.MinValue || plots[plot] != owner) return;

            zones[plot] = zone;
            pending.Push(plot);
        }

        // Lets another player act on all of an owner's land, or no longer. Returns whether it changed.
        public bool SetAllowed(string ownerId, string otherId, bool allowed)
        {
            if (string.IsNullOrEmpty(ownerId) || string.IsNullOrEmpty(otherId) || ownerId == otherId) return false;

            ClaimOwner owner = OwnerFor(ownerId);
            return allowed ? owner.Allowed.Add(otherId) : owner.Allowed.Remove(otherId);
        }

        // Every claimed plot, with its owner.
        public IEnumerable<(int plotX, int plotZ, ClaimOwner owner)> Claimed()
        {
            for (int z = 0; z < PlotsZ; z++)
            {
                for (int x = 0; x < PlotsX; x++)
                {
                    int owner = plots[z * PlotsX + x];
                    if (owner != Free) yield return (x, z, owners[owner]);
                }
            }
        }

        public void Write(Stream stream)
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(PlotsX);
                writer.Write(PlotsZ);
                writer.Write(owners.Count);
                foreach (ClaimOwner owner in owners)
                {
                    writer.Write(owner.PlayerId);
                    writer.Write(owner.Allowed.Count);
                    foreach (string playerId in owner.Allowed)
                    {
                        writer.Write(playerId);
                    }
                }

                for (int i = 0; i < plots.Length; i++)
                {
                    writer.Write(plots[i]);
                }
            }
        }

        // Throws InvalidDataException if the file is not valid, EndOfStreamException if it is truncated.
        public static Claims Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (reader.ReadUInt32() != Magic) throw new InvalidDataException("This file is not a claims save.");

                int format = reader.ReadInt32();
                if (format != FormatVersion) throw new InvalidDataException($"Unsupported claims save format {format} (expected: {FormatVersion}).");

                int plotsX = reader.ReadInt32();
                int plotsZ = reader.ReadInt32();
                if (plotsX <= 0 || plotsZ <= 0 || (long)plotsX * plotsZ > 1 << 24) throw new InvalidDataException("Invalid claims grid.");

                var claims = new Claims(plotsX, plotsZ);
                int ownerCount = reader.ReadInt32();
                if (ownerCount < 0) throw new InvalidDataException("Invalid claim owner count.");

                for (int i = 0; i < ownerCount; i++)
                {
                    string playerId = reader.ReadString();
                    if (playerId.Length == 0 || claims.ownersById.ContainsKey(playerId)) throw new InvalidDataException($"Invalid claim owner {i}.");

                    ClaimOwner owner = claims.OwnerFor(playerId);
                    int allowed = reader.ReadInt32();
                    if (allowed < 0) throw new InvalidDataException($"Invalid access list of claim owner {i}.");

                    for (int a = 0; a < allowed; a++)
                    {
                        owner.Allowed.Add(reader.ReadString());
                    }
                }

                for (int i = 0; i < claims.plots.Length; i++)
                {
                    int owner = reader.ReadInt32();
                    if (owner < Free || owner >= ownerCount) throw new InvalidDataException($"Invalid owner of plot {i}.");

                    claims.plots[i] = owner;
                    if (owner != Free) claims.owners[owner].PlotCount++;
                }

                return claims;
            }
        }

        public void SaveToFile(string path)
        {
            AtomicFile.Write(path, Write);
        }

        public static Claims LoadFromFile(string path)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                return Read(stream);
            }
        }

        private ClaimOwner OwnerFor(string playerId)
        {
            if (ownersById.TryGetValue(playerId, out ClaimOwner owner)) return owner;

            owner = new ClaimOwner(owners.Count, playerId);
            owners.Add(owner);
            ownersById.Add(playerId, owner);
            return owner;
        }
    }
}
