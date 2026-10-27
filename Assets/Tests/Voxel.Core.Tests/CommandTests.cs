using NUnit.Framework;
using Voxel.Core;

namespace Voxel.Core.Tests
{
    public class CommandTests
    {
        [Test]
        public void Teleport_ToAColumn_ToAPoint_OrToAPlayer()
        {
            Assert.IsTrue(Command.TryParse("/tp 120 -4", out Command column, out _));
            Assert.AreEqual(CommandKind.TeleportTo, column.Kind);
            Assert.AreEqual(120, column.X);
            Assert.AreEqual(-4, column.Z);
            Assert.IsNull(column.Y);

            Assert.IsTrue(Command.TryParse("TP 1 2 3", out Command point, out _));
            Assert.AreEqual(CommandKind.TeleportTo, point.Kind);
            Assert.AreEqual(2, point.Y);
            Assert.AreEqual(3, point.Z);

            Assert.IsTrue(Command.TryParse("teleport 2", out Command player, out _));
            Assert.AreEqual(CommandKind.TeleportToPlayer, player.Kind);
            Assert.AreEqual(2, player.Player);
        }

        [Test]
        public void Teleport_RefusesWordsAndWrongCounts()
        {
            Assert.IsFalse(Command.TryParse("/tp here", out _, out string notNumber));
            StringAssert.Contains("whole number", notNumber);
            Assert.IsFalse(Command.TryParse("/tp", out _, out _));
            Assert.IsFalse(Command.TryParse("/tp 1 2 3 4", out _, out _));
            Assert.IsFalse(Command.TryParse("/tp 1.5 2", out _, out _));
        }

        [Test]
        public void Spawn_NameInOneOrSeveralWords_WithOptionalCount()
        {
            Assert.IsTrue(Command.TryParse("/spawn Deer", out Command one, out _));
            Assert.AreEqual(CommandKind.Spawn, one.Kind);
            Assert.AreEqual("deer", one.Name);
            Assert.AreEqual(1, one.Count);

            Assert.IsTrue(Command.TryParse("spawn snow hare 5", out Command several, out _));
            Assert.AreEqual("snowhare", several.Name);
            Assert.AreEqual(5, several.Count);

            // A single word that happens to be a number is the name, not a count.
            Assert.IsTrue(Command.TryParse("spawn 3", out Command named, out _));
            Assert.AreEqual("3", named.Name);
        }

        [Test]
        public void Spawn_RefusesNoNameAndCountsOutOfRange()
        {
            Assert.IsFalse(Command.TryParse("/spawn", out _, out _));
            Assert.IsFalse(Command.TryParse("/spawn deer 0", out _, out _));
            Assert.IsFalse(Command.TryParse($"/spawn deer {Command.MaxSpawnCount + 1}", out _, out _));
        }

        [Test]
        public void HelpPlayersAndUnknownCommands()
        {
            Assert.IsTrue(Command.TryParse("/help", out Command help, out _));
            Assert.AreEqual(CommandKind.Help, help.Kind);
            Assert.IsTrue(Command.TryParse("players", out Command players, out _));
            Assert.AreEqual(CommandKind.Players, players.Kind);

            Assert.IsFalse(Command.TryParse("/fly", out _, out string unknown));
            StringAssert.Contains("Unknown command", unknown);
            Assert.IsFalse(Command.TryParse("   ", out _, out _));
        }

        [Test]
        public void Normalise_KeepsLettersAndDigitsInLowerCase()
        {
            Assert.AreEqual("tallgrass", Command.Normalise("Tall_Grass"));
            Assert.AreEqual("snowhare", Command.Normalise("SnowHare"));
            Assert.AreEqual("boisdechene", Command.Normalise("Bois de chêne"), "accents are dropped");
        }

        [Test]
        public void Give_AnItem_WithACountAndAPlayer()
        {
            Assert.IsTrue(Command.TryParse("/give oak wood 20 #3", out Command command, out _));
            Assert.AreEqual(CommandKind.Give, command.Kind);
            Assert.AreEqual("oakwood", command.Name);
            Assert.AreEqual(20, command.Count);
            Assert.AreEqual(3, command.Player);

            Assert.IsTrue(Command.TryParse("give Pioche", out command, out _));
            Assert.AreEqual("pioche", command.Name);
            Assert.AreEqual(1, command.Count);
            Assert.AreEqual(-1, command.Player, "to the one who typed it");

            Assert.IsFalse(Command.TryParse("/give", out _, out _));
            Assert.IsFalse(Command.TryParse("/give dirt 0", out _, out _));
            Assert.IsFalse(Command.TryParse($"/give dirt {Command.MaxGiveCount + 1}", out _, out _));
            Assert.IsFalse(Command.TryParse("/give dirt 5 #x", out _, out _));
        }
    }
}
