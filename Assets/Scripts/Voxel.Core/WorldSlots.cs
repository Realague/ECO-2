using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Voxel.Core
{
    // The worlds a host can play, each in its own folder under Saves in the data folder
    // (world.dat, players.dat, ecosystem.dat). The data folder itself is listed too when it holds a
    // world: games hosted before there was a list saved there, and the -host and -server command
    // line options still do.
    public static class WorldSlots
    {
        public const string FolderName = "Saves";
        public const string WorldFileName = "world.dat";

        // The name given to the world saved in the data folder itself.
        public const string LegacyName = "World";

        // A world name, as typed by the player, becomes a folder name of at most this length.
        public const int MaxNameLength = 40;

        public const string DefaultName = "New world";

        public struct Slot
        {
            public string Name;
            public string Directory;

            // Read from its world.dat. False for a folder whose world was never saved, or whose
            // save cannot be read: loading it then makes a new world.
            public bool HasSave;
            public int Seed;
            public int ChunksX;
            public int ChunksY;
            public int ChunksZ;

            // Made by another version of the terrain generator: loading it makes a new world.
            public bool IsOutdated;

            // When its world was last saved, in UTC.
            public DateTime LastPlayed;
        }

        // Every world of the data folder, the most recently played first.
        public static List<Slot> List(string dataDirectory, int generatorVersion)
        {
            var slots = new List<Slot>();
            if (File.Exists(Path.Combine(dataDirectory, WorldFileName)))
            {
                slots.Add(Read(LegacyName, dataDirectory, generatorVersion));
            }

            string folder = Path.Combine(dataDirectory, FolderName);
            if (Directory.Exists(folder))
            {
                foreach (string directory in Directory.GetDirectories(folder))
                {
                    slots.Add(Read(Path.GetFileName(directory), directory, generatorVersion));
                }
            }

            slots.Sort((a, b) =>
            {
                int byDate = b.LastPlayed.CompareTo(a.LastPlayed);
                return byDate != 0 ? byDate : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return slots;
        }

        private static Slot Read(string name, string directory, int generatorVersion)
        {
            var slot = new Slot { Name = name, Directory = directory, LastPlayed = Directory.GetLastWriteTimeUtc(directory) };
            string path = Path.Combine(directory, WorldFileName);
            if (!File.Exists(path)) return slot;

            slot.LastPlayed = File.GetLastWriteTimeUtc(path);
            try
            {
                WorldSave.Header header = WorldSave.ReadHeaderFromFile(path);
                slot.HasSave = true;
                slot.Seed = header.Seed;
                slot.ChunksX = header.ChunksX;
                slot.ChunksY = header.ChunksY;
                slot.ChunksZ = header.ChunksZ;
                slot.IsOutdated = header.GeneratorVersion != generatorVersion;
            }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            {
                // Listed all the same, without what the save would have told.
            }

            return slot;
        }

        // Creates the folder of a new world named so, and returns it. The name loses what a folder
        // name cannot hold, and gets a number if another world has it already.
        public static string CreateDirectory(string dataDirectory, string name)
        {
            string folder = Path.Combine(dataDirectory, FolderName);
            string baseName = FolderNameOf(name);
            string candidate = baseName;
            for (int number = 2; Directory.Exists(Path.Combine(folder, candidate)) || IsLegacyName(dataDirectory, candidate); number++)
            {
                candidate = $"{baseName} ({number})";
            }

            string directory = Path.Combine(folder, candidate);
            Directory.CreateDirectory(directory);
            return directory;
        }

        // The world in the data folder itself is listed as LegacyName: a new world does not take it too.
        private static bool IsLegacyName(string dataDirectory, string name)
        {
            return string.Equals(name, LegacyName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(dataDirectory, WorldFileName));
        }

        // A name as typed, made fit to be a folder name on every system: without the characters
        // folders refuse (and without dots or spaces at its ends, which Windows drops), spaces
        // gathered, at most MaxNameLength long, DefaultName if nothing is left.
        public static string FolderNameOf(string name)
        {
            var text = new StringBuilder();
            bool space = false;
            foreach (char c in name ?? "")
            {
                if (char.IsWhiteSpace(c))
                {
                    space = text.Length > 0;
                    continue;
                }

                if (Array.IndexOf(ForbiddenCharacters, c) >= 0 || char.IsControl(c)) continue;

                if (space) text.Append(' ');
                space = false;
                text.Append(c);
                if (text.Length >= MaxNameLength) break;
            }

            string result = text.ToString().Trim().Trim('.').Trim();
            if (result.Length == 0 || IsReservedName(result)) return DefaultName;

            return result;
        }

        private static readonly char[] ForbiddenCharacters = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        // Names Windows keeps for devices, whatever the extension.
        private static bool IsReservedName(string name)
        {
            string stem = name.Split('.')[0].Trim().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL") return true;

            return stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] >= '1' && stem[3] <= '9';
        }

        // The seed a player typed: a whole number is that number; any other text gives a number
        // of its own, always the same for the same text; nothing gives `random`.
        public static int SeedOf(string text, int random)
        {
            string trimmed = (text ?? "").Trim();
            if (trimmed.Length == 0) return random;
            if (int.TryParse(trimmed, out int number)) return number;

            // FNV-1a over the characters: stable on every machine and every run, unlike string.GetHashCode.
            uint hash = 2166136261;
            foreach (char c in trimmed)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return unchecked((int)hash);
        }
    }
}
