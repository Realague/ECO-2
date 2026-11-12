using System;
using System.Globalization;

namespace Voxel.Core
{
    // A bonus's value worked out from a little formula (SkillBonus.Formula): numbers, + - * / ^,
    // parentheses, min(a, b), max(a, b) and the variables step (the step reached, from 1), steps (the
    // upgrade's step count) and tier (its tier, 1 to 5). "5 * step" gives 5, 10, 15… Read the same
    // way on every machine (invariant numbers, doubles).
    public static class SkillFormula
    {
        public static double Evaluate(string formula, double step, double steps, double tier)
        {
            var parser = new Parser(formula, step, steps, tier);
            double value = parser.Expression();
            parser.ExpectEnd();
            return value;
        }

        // Whether a formula reads, and why not.
        public static bool TryValidate(string formula, out string error)
        {
            try
            {
                Evaluate(formula, 1, 1, 1);
                error = null;
                return true;
            }
            catch (FormatException e)
            {
                error = e.Message;
                return false;
            }
        }

        private sealed class Parser
        {
            private readonly string text;
            private readonly double step;
            private readonly double steps;
            private readonly double tier;
            private int at;

            public Parser(string text, double step, double steps, double tier)
            {
                this.text = text ?? "";
                this.step = step;
                this.steps = steps;
                this.tier = tier;
            }

            public void ExpectEnd()
            {
                Skip();
                if (at < text.Length) throw new FormatException($"Unexpected '{text[at]}' at {at} in \"{text}\".");
            }

            // a + b - c
            public double Expression()
            {
                double value = Term();
                while (true)
                {
                    if (Take('+')) value += Term();
                    else if (Take('-')) value -= Term();
                    else return value;
                }
            }

            // a * b / c
            private double Term()
            {
                double value = Power();
                while (true)
                {
                    if (Take('*')) value *= Power();
                    else if (Take('/')) value /= Power();
                    else return value;
                }
            }

            // a ^ b, right to left
            private double Power()
            {
                double value = Unary();
                return Take('^') ? Math.Pow(value, Power()) : value;
            }

            private double Unary()
            {
                if (Take('-')) return -Unary();
                if (Take('+')) return Unary();
                return Primary();
            }

            private double Primary()
            {
                Skip();
                if (Take('('))
                {
                    double inner = Expression();
                    Expect(')');
                    return inner;
                }

                if (at < text.Length && (char.IsDigit(text[at]) || text[at] == '.')) return Number();
                if (at < text.Length && char.IsLetter(text[at])) return Name();
                throw new FormatException(at < text.Length ? $"Unexpected '{text[at]}' at {at} in \"{text}\"." : $"\"{text}\" ends too soon.");
            }

            private double Number()
            {
                int start = at;
                while (at < text.Length && (char.IsDigit(text[at]) || text[at] == '.')) at++;
                return double.Parse(text.Substring(start, at - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            private double Name()
            {
                int start = at;
                while (at < text.Length && char.IsLetter(text[at])) at++;
                string name = text.Substring(start, at - start).ToLowerInvariant();
                switch (name)
                {
                    case "step": return step;
                    case "steps": return steps;
                    case "tier": return tier;
                    case "min":
                    case "max":
                        Expect('(');
                        double a = Expression();
                        Expect(',');
                        double b = Expression();
                        Expect(')');
                        return name == "min" ? Math.Min(a, b) : Math.Max(a, b);
                    default:
                        throw new FormatException($"Unknown name \"{name}\" in \"{text}\".");
                }
            }

            private void Skip()
            {
                while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
            }

            private bool Take(char c)
            {
                Skip();
                if (at >= text.Length || text[at] != c) return false;
                at++;
                return true;
            }

            private void Expect(char c)
            {
                if (!Take(c)) throw new FormatException($"'{c}' expected at {at} in \"{text}\".");
            }
        }
    }
}
