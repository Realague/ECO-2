using System;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class SkillFormulaTests
    {
        [Test]
        public void Formulas_ReadNumbersOperatorsAndVariables()
        {
            Assert.AreEqual(15, SkillFormula.Evaluate("5 * step", 3, 5, 1), 1e-9);
            Assert.AreEqual(7, SkillFormula.Evaluate("1 + 2 * 3", 1, 1, 1), 1e-9, "products first");
            Assert.AreEqual(9, SkillFormula.Evaluate("(1 + 2) * 3", 1, 1, 1), 1e-9);
            Assert.AreEqual(8, SkillFormula.Evaluate("2 ^ 3", 1, 1, 1), 1e-9);
            Assert.AreEqual(-4, SkillFormula.Evaluate("-step - 1", 3, 1, 1), 1e-9);
            Assert.AreEqual(2.5, SkillFormula.Evaluate("steps / 2", 1, 5, 1), 1e-9);
            Assert.AreEqual(12.5, SkillFormula.Evaluate("min(25, 2.5 * tier)", 1, 1, 5), 1e-9, "invariant decimal point");
            Assert.AreEqual(25, SkillFormula.Evaluate("max(25, tier)", 1, 1, 5), 1e-9);
        }

        [Test]
        public void BadFormulas_AreRefused()
        {
            Assert.IsFalse(SkillFormula.TryValidate("5 *", out string error));
            Assert.IsNotNull(error);
            Assert.IsFalse(SkillFormula.TryValidate("(1 + 2", out _));
            Assert.IsFalse(SkillFormula.TryValidate("level * 2", out _), "unknown variable");
            Assert.IsFalse(SkillFormula.TryValidate("1 2", out _));
            Assert.IsTrue(SkillFormula.TryValidate("5 * step", out _));
            Assert.Throws<FormatException>(() => SkillFormula.Evaluate("", 1, 1, 1));
        }

        [Test]
        public void ABonus_UsesItsFormula_ElseItsValue()
        {
            var fixedBonus = new SkillBonus { Value = 7 };
            var formula = new SkillBonus { Value = 7, Formula = "5 * step" };
            Assert.AreEqual(7, fixedBonus.ValueAt(3, 5, 3), 1e-9);
            Assert.AreEqual(15, formula.ValueAt(3, 5, 3), 1e-9);
        }
    }
}
