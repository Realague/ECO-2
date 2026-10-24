using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Voxel.Client
{
    // The text shown to players, in their language. English is the language of the code: every text
    // is written in English where it is used, and that English text is its own key. Each other
    // language has a table (Assets/Resources/Localization/<code>.txt, LocTable) giving the
    // translation of each English text; a text missing from it is shown in English.
    //
    //   Loc.T("Too hungry: eat something")       a fixed text
    //   Loc.T("Too cold for {0}", name)          a text with values, as string.Format
    //
    // Values that are names (species, items) are given twice when a language needs them in another
    // form: Loc.Name(english) gives the translated name, Loc.Lower its lower-case form (English
    // writes "Oak wood", French "Bois de chêne"). The UXML documents keep their English texts:
    // LocalizedDocuments translates them (Apply). The server sends English (or codes), and each
    // client translates what it shows, so every player reads their own language.
    //
    // The language is the computer's on first start if it has a table, else English, then the one
    // chosen in the main menu's Options (kept in PlayerPrefs).
    public static class Loc
    {
        public const string English = "en";
        private const string PrefKey = "language";
        private const string TableFolder = "Localization";

        // The languages with a table, and English; the name each is shown by, in itself.
        public static readonly (string Code, string Name)[] Languages = { (English, "English"), ("fr", "Français") };

        private static readonly ConditionalWeakTable<TextElement, string> Originals = new ConditionalWeakTable<TextElement, string>();
        private static readonly ConditionalWeakTable<TextField, string> OriginalPlaceholders = new ConditionalWeakTable<TextField, string>();
        private static readonly ConditionalWeakTable<VisualElement, string> OriginalTooltips = new ConditionalWeakTable<VisualElement, string>();
        private static readonly Regex FrenchElision = new Regex(@"\b([Dd]e|[Ll]e|[Ll]a) (?=[aeiouyàâéèêëîïôûAEIOUYÉ])", RegexOptions.Compiled);

        private static LocTable table;
        private static bool initialized;

        public static string Language { get; private set; } = English;

        // How numbers are written in the language ("26.3" in English, "26,3" in French).
        public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

        // A number as the language writes it, in the given format ("0.0", "N0"...).
        public static string Number(float value, string format) => value.ToString(format, Culture);

        public static string Number(int value) => value.ToString("N0", Culture);

        // Raised when the language changes: what was translated is to be translated again.
        public static event Action Changed;

        // The language chosen before, else the computer's if it has a table, else English.
        public static void Initialize()
        {
            if (initialized) return;

            initialized = true;
            string code = PlayerPrefs.GetString(PrefKey, "");
            if (!IsKnown(code)) code = Application.systemLanguage == SystemLanguage.French ? "fr" : English;
            Load(code);
        }

        public static void SetLanguage(string code)
        {
            if (!IsKnown(code)) code = English;
            initialized = true;
            PlayerPrefs.SetString(PrefKey, code);
            PlayerPrefs.Save();
            if (code == Language && (code == English || table != null)) return;

            Load(code);
            Changed?.Invoke();
        }

        public static string T(string english)
        {
            if (!initialized) Initialize();
            if (string.IsNullOrEmpty(english) || table == null) return english;

            return table.TryGet(english, out string translated) ? translated : english;
        }

        public static string T(string english, params object[] values)
        {
            string format = T(english);
            string text;
            try
            {
                text = string.Format(Culture, format, values);
            }
            catch (FormatException)
            {
                // A translation whose places do not match its English: the English, rather than nothing.
                text = string.Format(Culture, english, values);
            }

            return Language == "fr" ? FrenchElision.Replace(text, Elide) : text;
        }

        // A name (species, item, biome...), translated.
        public static string Name(string english) => T(english);

        // A text that one English word cannot tell apart ("Strawberry", the plant or its fruit): the
        // table's "context|English" if it has one, else the plain translation.
        public static string In(string context, string english)
        {
            if (!initialized) Initialize();
            if (table != null && table.TryGet(context + "|" + english, out string translated)) return translated;

            return T(english);
        }

        // A translated name in lower case, for the middle of a sentence ("Bois de chêne").
        public static string Lower(string english) => T(english).ToLower(Culture);

        // Translates the texts of a document as written in its UXML: each label, button and field
        // placeholder is translated from its first text (kept, so that it can be translated again).
        // Elements whose text the code sets later are left alone if they were empty at first.
        public static void Apply(VisualElement root)
        {
            if (root == null) return;

            root.Query<TextElement>().ForEach(element =>
            {
                if (!Originals.TryGetValue(element, out string original))
                {
                    original = element.text;
                    if (string.IsNullOrEmpty(original)) return;

                    Originals.Add(element, original);
                }

                element.text = T(original);
            });

            root.Query<VisualElement>().ForEach(element =>
            {
                if (!OriginalTooltips.TryGetValue(element, out string original))
                {
                    original = element.tooltip;
                    if (string.IsNullOrEmpty(original)) return;

                    OriginalTooltips.Add(element, original);
                }

                element.tooltip = T(original);
            });

            root.Query<TextField>().ForEach(field =>
            {
                if (!OriginalPlaceholders.TryGetValue(field, out string original))
                {
                    original = field.textEdition.placeholder;
                    if (string.IsNullOrEmpty(original)) return;

                    OriginalPlaceholders.Add(field, original);
                }

                field.textEdition.placeholder = T(original);
            });
        }

        private static bool IsKnown(string code)
        {
            foreach ((string known, string _) in Languages)
            {
                if (known == code) return true;
            }

            return false;
        }

        private static void Load(string code)
        {
            Language = code;
            table = null;
            Culture = CultureInfo.InvariantCulture;
            if (code == English) return;

            var asset = Resources.Load<TextAsset>($"{TableFolder}/{code}");
            if (asset == null)
            {
                Debug.LogWarning($"No text table for the language {code}: the game is shown in English.");
                Language = English;
                return;
            }

            try
            {
                Culture = CultureInfo.GetCultureInfo(code);
            }
            catch (CultureNotFoundException)
            {
                Culture = CultureInfo.InvariantCulture;
            }

            table = LocTable.Parse(asset.text, out int line, out string problem);
            if (problem != null) Debug.LogWarning($"Localization/{code}.txt, line {line}: {problem}");
        }

        // French writes "d'avoine", "l'arbre": "de", "le" and "la" before a vowel lose their vowel (not before an h:
        // the h words of the game, héron, haricot, hautes herbes, keep "de").
        private static string Elide(Match match) => match.Groups[1].Value.Substring(0, 1) + "'";
    }
}
