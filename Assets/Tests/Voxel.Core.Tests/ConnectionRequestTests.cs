using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ConnectionRequestTests
    {
        [Test]
        public void ToBytesThenTryParse_RestoresPlayerIdAndPassword()
        {
            byte[] data = new ConnectionRequest("3f2a9c0e-player_1", "s3cret!é").ToBytes();

            bool parsed = ConnectionRequest.TryParse(data, out ConnectionRequest request);

            Assert.IsTrue(parsed);
            Assert.AreEqual("3f2a9c0e-player_1", request.PlayerId);
            Assert.AreEqual("s3cret!é", request.Password);
        }

        [Test]
        public void TryParse_EmptyPassword_IsAccepted()
        {
            byte[] data = new ConnectionRequest("abc", "").ToBytes();

            Assert.IsTrue(ConnectionRequest.TryParse(data, out ConnectionRequest request));
            Assert.AreEqual("", request.Password);
        }

        [Test]
        public void TryParse_NullEmptyOrTruncatedData_IsRefused()
        {
            byte[] valid = new ConnectionRequest("abc", "password").ToBytes();
            var truncated = new byte[valid.Length - 3];
            System.Array.Copy(valid, truncated, truncated.Length);

            Assert.IsFalse(ConnectionRequest.TryParse(null, out _));
            Assert.IsFalse(ConnectionRequest.TryParse(new byte[0], out _));
            Assert.IsFalse(ConnectionRequest.TryParse(truncated, out _));
        }

        [Test]
        public void TryParse_UnknownFormatVersion_IsRefused()
        {
            byte[] data = new ConnectionRequest("abc", "").ToBytes();
            data[0] = 99;

            Assert.IsFalse(ConnectionRequest.TryParse(data, out _));
        }

        [Test]
        public void TryParse_InvalidPlayerIdOrOverlongPassword_IsRefused()
        {
            byte[] badId = new ConnectionRequest("../players", "").ToBytes();
            byte[] emptyId = new ConnectionRequest("", "").ToBytes();
            byte[] longPassword = new ConnectionRequest("abc", new string('x', ConnectionRequest.MaxLength + 1)).ToBytes();

            Assert.IsFalse(ConnectionRequest.TryParse(badId, out _));
            Assert.IsFalse(ConnectionRequest.TryParse(emptyId, out _));
            Assert.IsFalse(ConnectionRequest.TryParse(longPassword, out _));
        }

        [TestCase("abc", true)]
        [TestCase("A-b_9", true)]
        [TestCase("", false)]
        [TestCase(null, false)]
        [TestCase("with space", false)]
        [TestCase("naïve", false)]
        public void IsValidPlayerId_AcceptsOnlyLettersDigitsDashAndUnderscore(string playerId, bool expected)
        {
            Assert.AreEqual(expected, ConnectionRequest.IsValidPlayerId(playerId));
        }

        [Test]
        public void IsValidPlayerId_RefusesOverlongIds()
        {
            Assert.IsTrue(ConnectionRequest.IsValidPlayerId(new string('a', ConnectionRequest.MaxLength)));
            Assert.IsFalse(ConnectionRequest.IsValidPlayerId(new string('a', ConnectionRequest.MaxLength + 1)));
        }
    }
}
