using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class StuckArrowsTests
    {
        [Test]
        public void Arrows_AreAddedAndPickedUp_ByIdentifier()
        {
            var arrows = new StuckArrows();
            StuckArrow first = arrows.Add(new Vector3(1f, 2f, 3f), Vector3.UnitX);
            StuckArrow second = arrows.Add(new Vector3(4f, 5f, 6f), Vector3.UnitZ);

            Assert.AreNotEqual(first.Id, second.Id);
            Assert.AreEqual(2, arrows.Count);
            Assert.IsTrue(arrows.Remove(first.Id));
            Assert.IsFalse(arrows.Remove(first.Id), "picked up once");
            Assert.IsTrue(arrows.TryGet(second.Id, out StuckArrow kept));
            Assert.AreEqual(new Vector3(4f, 5f, 6f), kept.Position);
        }

        [Test]
        public void WriteThenRead_KeepsTheArrows_AndNeverReusesAnIdentifier()
        {
            var arrows = new StuckArrows();
            arrows.Add(new Vector3(1f, 2f, 3f), Vector3.UnitX);
            StuckArrow last = arrows.Add(new Vector3(4f, 5f, 6f), new Vector3(0f, -0.5f, 1f));
            arrows.Remove(last.Id);
            var stream = new MemoryStream();
            arrows.Write(stream);
            stream.Position = 0;

            StuckArrows loaded = StuckArrows.Read(stream);

            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), loaded.All.Single().Position);
            Assert.Greater(loaded.Add(Vector3.Zero, Vector3.UnitY).Id, last.Id);
        }

        [Test]
        public void Read_RefusesWhatIsNotAnArrowSave()
        {
            Assert.Throws<InvalidDataException>(() => StuckArrows.Read(new MemoryStream(new byte[16])));
        }
    }
}
