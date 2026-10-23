using System;
using System.Text;

namespace Voxel.Core
{
    // The name a player chooses, shown to the others (storage access lists, labels): 2 to 16
    // characters, letters (accented ones too), digits, single spaces, '_' and '-'. Names are unique on
    // a server, ignoring case.
    public static class PlayerNames
    {
        public const int MinLength = 2;
        public const int MaxLength = 16;

        // The name as it is kept: spaces at both ends removed. False if it is not a valid name.
        public static bool TryNormalize(string text, out string name)
        {
            name = null;
            if (text == null) return false;

            string trimmed = text.Trim();
            if (trimmed.Length < MinLength || trimmed.Length > MaxLength) return false;

            var builder = new StringBuilder(trimmed.Length);
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                bool allowed = char.IsLetterOrDigit(c) || c == '_' || c == '-' || (c == ' ' && trimmed[i - 1] != ' ');
                if (!allowed || char.IsSurrogate(c)) return false;

                builder.Append(c);
            }

            name = builder.ToString();
            return true;
        }

        public static bool SameName(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
