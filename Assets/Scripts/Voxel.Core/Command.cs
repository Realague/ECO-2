using System;
using System.Collections.Generic;
using System.Globalization;

namespace Voxel.Core
{
    public enum CommandKind
    {
        Help,
        Players,
        TeleportTo,
        TeleportToPlayer,
        Spawn
    }

    // A command typed by a player, as the server runs it (CommandSync). The text may start with "/";
    // words are separated by spaces and case does not matter:
    //   help                    the list of commands
    //   players                 the connected players, with their numbers and positions
    //   tp <x> <z>              to that column, standing on its top
    //   tp <x> <y> <z>          to that point (raised out of any block)
    //   tp <player number>      to that player
    //   spawn <name> [count]    animals or plants of a species, where the player aims
    // A species name may be written in several words ("snow hare" is SnowHare).
    public sealed class Command
    {
        public const int MaxLength = 128;
        public const int MaxSpawnCount = 20;

        public const string HelpText =
            "Commands: /spawn <animal or plant> [count], /tp <x> <z>, /tp <x> <y> <z>, /tp <player number>, /players, /help";

        public CommandKind Kind { get; private set; }

        // TeleportTo: the column, and the height if given.
        public int X { get; private set; }
        public int? Y { get; private set; }
        public int Z { get; private set; }

        // TeleportToPlayer: the player's number.
        public int Player { get; private set; }

        // Spawn: the species, letters and digits only, lower case ("snowhare"), and how many.
        public string Name { get; private set; }
        public int Count { get; private set; } = 1;

        public static bool TryParse(string text, out Command command, out string error)
        {
            command = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = HelpText;
                return false;
            }

            text = text.Trim();
            if (text.StartsWith("/", StringComparison.Ordinal)) text = text.Substring(1);
            string[] words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                error = HelpText;
                return false;
            }

            string verb = words[0].ToLowerInvariant();
            switch (verb)
            {
                case "help":
                case "?":
                    command = new Command { Kind = CommandKind.Help };
                    return true;

                case "players":
                case "list":
                    command = new Command { Kind = CommandKind.Players };
                    return true;

                case "tp":
                case "teleport":
                    return TryParseTeleport(words, out command, out error);

                case "spawn":
                case "summon":
                    return TryParseSpawn(words, out command, out error);

                default:
                    error = $"Unknown command '{words[0]}'. {HelpText}";
                    return false;
            }
        }

        // A species or other name reduced to what is compared: letters and digits, lower case.
        public static string Normalise(string name)
        {
            var kept = new List<char>(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) kept.Add(char.ToLowerInvariant(c));
            }

            return new string(kept.ToArray());
        }

        private static bool TryParseTeleport(string[] words, out Command command, out string error)
        {
            command = null;
            error = null;
            var numbers = new int[words.Length - 1];
            for (int i = 1; i < words.Length; i++)
            {
                if (!int.TryParse(words[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i - 1]))
                {
                    error = $"'{words[i]}' is not a whole number. Use /tp <x> <z>, /tp <x> <y> <z> or /tp <player number>.";
                    return false;
                }
            }

            switch (numbers.Length)
            {
                case 1:
                    command = new Command { Kind = CommandKind.TeleportToPlayer, Player = numbers[0] };
                    return true;
                case 2:
                    command = new Command { Kind = CommandKind.TeleportTo, X = numbers[0], Z = numbers[1] };
                    return true;
                case 3:
                    command = new Command { Kind = CommandKind.TeleportTo, X = numbers[0], Y = numbers[1], Z = numbers[2] };
                    return true;
                default:
                    error = "Use /tp <x> <z>, /tp <x> <y> <z> or /tp <player number>.";
                    return false;
            }
        }

        private static bool TryParseSpawn(string[] words, out Command command, out string error)
        {
            command = null;
            error = null;
            int last = words.Length - 1;
            int count = 1;
            if (last >= 2 && int.TryParse(words[last], NumberStyles.Integer, CultureInfo.InvariantCulture, out int given))
            {
                count = given;
                last--;
            }

            string name = Normalise(string.Join(" ", words, 1, Math.Max(0, last)));
            if (name.Length == 0)
            {
                error = "Use /spawn <animal or plant> [count], for instance /spawn deer 3.";
                return false;
            }

            if (count < 1 || count > MaxSpawnCount)
            {
                error = $"The count must be between 1 and {MaxSpawnCount}.";
                return false;
            }

            command = new Command { Kind = CommandKind.Spawn, Name = name, Count = count };
            return true;
        }
    }
}
