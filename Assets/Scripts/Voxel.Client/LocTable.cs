using System.Collections.Generic;
using System.Text;

namespace Voxel.Client
{
    // One language's translations (Loc), read from a plain text file, one per line:
    //
    //   # a comment
    //   English text<TAB>Translation
    //
    // so that it can be edited in any text editor or spreadsheet. "\n" and "\t" stand for a line break
    // and a tab; "{0}" and the like are the places of values, in any order. Blank lines and comments
    // are skipped; a line without a tab, or an English text given twice, is reported (the first
    // problem only) and skipped.
    public sealed class LocTable
    {
        private readonly Dictionary<string, string> texts = new Dictionary<string, string>();

        public int Count => texts.Count;

        public bool TryGet(string english, out string translated) => texts.TryGetValue(english, out translated);

        // problem: the first line that could not be read and why (null if none).
        public static LocTable Parse(string content, out int problemLine, out string problem)
        {
            var table = new LocTable();
            problemLine = 0;
            problem = null;
            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0 || line[0] == '#' || line.Trim().Length == 0) continue;

                int tab = line.IndexOf('\t');
                string reason = null;
                if (tab <= 0 || tab == line.Length - 1) reason = "expected an English text, a tab and its translation";
                else
                {
                    string english = Unescape(line.Substring(0, tab));
                    string translated = Unescape(line.Substring(tab + 1).TrimEnd());
                    if (table.texts.ContainsKey(english)) reason = $"\"{english}\" is translated twice";
                    else table.texts.Add(english, translated);
                }

                if (reason != null && problem == null)
                {
                    problemLine = i + 1;
                    problem = reason;
                }
            }

            return table;
        }

        private static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0) return text;

            var result = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char next = text[i + 1];
                    if (next == 'n' || next == 't' || next == '\\')
                    {
                        result.Append(next == 'n' ? '\n' : next == 't' ? '\t' : '\\');
                        i++;
                        continue;
                    }
                }

                result.Append(c);
            }

            return result.ToString();
        }
    }
}
