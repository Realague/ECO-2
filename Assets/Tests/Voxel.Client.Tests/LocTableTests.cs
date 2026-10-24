using System;
using System.IO;
using NUnit.Framework;

namespace Voxel.Client.Tests
{
    public class LocTableTests
    {
        [Test]
        public void ALine_IsAnEnglishText_ATab_AndItsTranslation()
        {
            LocTable table = LocTable.Parse("# a comment\n\nHello\tBonjour\r\nTwo lines\tDeux\\nlignes\n", out _, out string problem);

            Assert.IsNull(problem);
            Assert.AreEqual(2, table.Count);
            Assert.IsTrue(table.TryGet("Hello", out string hello));
            Assert.AreEqual("Bonjour", hello);
            Assert.IsTrue(table.TryGet("Two lines", out string lines));
            Assert.AreEqual("Deux\nlignes", lines);
            Assert.IsFalse(table.TryGet("Missing", out _));
        }

        [Test]
        public void ABadLine_IsReported_AndSkipped()
        {
            LocTable table = LocTable.Parse("Hello\tBonjour\nNo tab here\nHello\tSalut\n", out int line, out string problem);

            Assert.AreEqual(2, line, "the first problem");
            Assert.IsNotNull(problem);
            Assert.IsTrue(table.TryGet("Hello", out string hello));
            Assert.AreEqual("Bonjour", hello, "the first translation of a text given twice is kept");
        }

        // The French table shipped with the game reads without a problem, and each of its texts
        // formats with the values the game gives (at most four, {0} to {3}).
        [Test]
        public void TheFrenchTable_ReadsCleanly_AndEveryTextFormats()
        {
            string path = Path.Combine("Assets", "Resources", "Localization", "fr.txt");
            string content = File.ReadAllText(path);
            LocTable table = LocTable.Parse(content, out int line, out string problem);

            Assert.IsNull(problem, $"line {line}: {problem}");
            Assert.Greater(table.Count, 300);
            foreach (string raw in content.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;

                string translated = raw.Substring(raw.IndexOf('\t') + 1);
                Assert.DoesNotThrow(() => string.Format(translated, "a", "b", "c", "d"), translated);
            }
        }
    }
}
