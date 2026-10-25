using System.IO;
using NUnit.Framework;

namespace Voxel.Core.Tests
{
    public class ClaimsTests
    {
        private const string Alice = "alice";
        private const string Bob = "bob";
        private const string Carol = "carol";

        [Test]
        public void PlotOf_GroupsColumnsBySquaresOfEight()
        {
            Assert.AreEqual(0, ClaimRules.PlotOf(0));
            Assert.AreEqual(0, ClaimRules.PlotOf(7));
            Assert.AreEqual(1, ClaimRules.PlotOf(8));
            Assert.AreEqual(-1, ClaimRules.PlotOf(-1));
            Assert.AreEqual(-1, ClaimRules.PlotOf(-8));
            Assert.AreEqual(-2, ClaimRules.PlotOf(-9));
            Assert.AreEqual(128, ClaimRules.PlotsAcross(1024));
            Assert.AreEqual(2, ClaimRules.PlotsAcross(9));
        }

        [Test]
        public void AFreePlot_CanBeClaimed_ButNotTwice()
        {
            Claims claims = Claims.ForWorld(64, 64);

            Assert.IsTrue(claims.TryClaim(Alice, 2, 3, out ClaimRefusal refusal));
            Assert.AreEqual(ClaimRefusal.None, refusal);
            Assert.AreEqual(Alice, claims.OwnerOfPlot(2, 3).PlayerId);
            Assert.AreEqual(1, claims.CountOf(Alice));

            Assert.IsFalse(claims.TryClaim(Bob, 2, 3, out refusal));
            Assert.AreEqual(ClaimRefusal.Taken, refusal);
            Assert.IsFalse(claims.TryClaim(Alice, 2, 3, out refusal), "already hers");
            Assert.IsFalse(claims.TryClaim(Alice, 8, 0, out refusal));
            Assert.AreEqual(ClaimRefusal.OutsideWorld, refusal);
            Assert.IsFalse(claims.TryClaim("", 0, 0, out refusal));
            Assert.AreEqual(ClaimRefusal.NoName, refusal);
        }

        [Test]
        public void APlayer_ClaimsNoMoreThanTheQuota()
        {
            Claims claims = Claims.ForWorld(256, 256);
            for (int i = 0; i < ClaimRules.MaxPlotsPerPlayer; i++)
            {
                Assert.IsTrue(claims.TryClaim(Alice, i, 0, out _), $"plot {i}");
            }

            Assert.IsFalse(claims.TryClaim(Alice, 0, 1, out ClaimRefusal refusal));
            Assert.AreEqual(ClaimRefusal.QuotaReached, refusal);

            Assert.IsTrue(claims.TryRelease(Alice, 3, 0, out _));
            Assert.AreEqual(ClaimRules.MaxPlotsPerPlayer - 1, claims.CountOf(Alice));
            Assert.IsTrue(claims.TryClaim(Alice, 0, 1, out _), "a released plot frees a place in the quota");
        }

        [Test]
        public void OnlyTheOwner_ReleasesAPlot()
        {
            Claims claims = Claims.ForWorld(64, 64);
            claims.TryClaim(Alice, 1, 1, out _);

            Assert.IsFalse(claims.TryRelease(Bob, 1, 1, out ClaimRefusal refusal));
            Assert.AreEqual(ClaimRefusal.NotYours, refusal);
            Assert.IsFalse(claims.TryRelease(Bob, 2, 2, out refusal), "a free plot is nobody's to release");
            Assert.AreEqual(ClaimRefusal.NotYours, refusal);

            Assert.IsTrue(claims.TryRelease(Alice, 1, 1, out _));
            Assert.IsNull(claims.OwnerOfPlot(1, 1));
            Assert.AreEqual(0, claims.CountOf(Alice));
        }

        [Test]
        public void OnClaimedLand_OnlyTheOwnerAndThoseSheAllows_MayAct()
        {
            Claims claims = Claims.ForWorld(64, 64);
            claims.TryClaim(Alice, 1, 0, out _);

            // Plot 1 covers columns 8 to 15.
            Assert.IsTrue(claims.CanAct(Alice, 8, 0));
            Assert.IsTrue(claims.CanAct(Alice, 15, 7));
            Assert.IsFalse(claims.CanAct(Bob, 8, 0));
            Assert.IsFalse(claims.CanAct(Bob, 15, 7));
            Assert.IsFalse(claims.CanAct("", 12, 3), "nobody without an id");
            Assert.IsTrue(claims.CanAct(Bob, 7, 0), "the column next to it is free");
            Assert.IsTrue(claims.CanAct(Bob, 16, 0));
            Assert.IsTrue(claims.CanAct(Bob, 8, 8));
            Assert.IsTrue(claims.CanAct(Bob, -3, 200), "outside the world");

            Assert.IsTrue(claims.SetAllowed(Alice, Bob, true));
            Assert.IsFalse(claims.SetAllowed(Alice, Bob, true), "already allowed");
            Assert.IsFalse(claims.SetAllowed(Alice, Alice, true), "she needs no leave");
            Assert.IsTrue(claims.CanAct(Bob, 8, 0));
            Assert.IsFalse(claims.CanAct(Carol, 8, 0));

            claims.TryClaim(Alice, 3, 3, out _);
            Assert.IsTrue(claims.CanAct(Bob, 26, 29), "allowed on all her land, new plots too");

            Assert.IsTrue(claims.SetAllowed(Alice, Bob, false));
            Assert.IsFalse(claims.CanAct(Bob, 8, 0));
        }

        [Test]
        public void Claims_AreSavedAndLoaded()
        {
            Claims claims = Claims.ForWorld(64, 48);
            claims.TryClaim(Alice, 1, 0, out _);
            claims.TryClaim(Alice, 7, 5, out _);
            claims.TryClaim(Bob, 4, 4, out _);
            claims.SetAllowed(Alice, Carol, true);
            claims.SetAllowed(Carol, Bob, true);

            var stream = new MemoryStream();
            claims.Write(stream);
            stream.Position = 0;
            Claims loaded = Claims.Read(stream);

            Assert.AreEqual(8, loaded.PlotsX);
            Assert.AreEqual(6, loaded.PlotsZ);
            Assert.AreEqual(Alice, loaded.OwnerOfPlot(1, 0).PlayerId);
            Assert.AreEqual(Alice, loaded.OwnerOfPlot(7, 5).PlayerId);
            Assert.AreEqual(Bob, loaded.OwnerOfPlot(4, 4).PlayerId);
            Assert.IsNull(loaded.OwnerOfPlot(0, 0));
            Assert.AreEqual(2, loaded.CountOf(Alice));
            Assert.AreEqual(1, loaded.CountOf(Bob));
            Assert.AreEqual(0, loaded.CountOf(Carol), "an owner with no plot keeps her access list");
            Assert.IsTrue(loaded.CanAct(Carol, 8, 0));
            Assert.IsTrue(loaded.TryGetOwner(Carol, out ClaimOwner carol));
            Assert.IsTrue(carol.Allowed.Contains(Bob));
        }

        [Test]
        public void PlotsOfOneOwner_JoinedBySides_AreOneZone()
        {
            Claims claims = Claims.ForWorld(64, 64);
            claims.TryClaim(Alice, 1, 1, out _);
            claims.TryClaim(Alice, 2, 1, out _);
            claims.TryClaim(Alice, 2, 2, out _);
            claims.TryClaim(Alice, 3, 3, out _);
            claims.TryClaim(Bob, 1, 2, out _);

            int zone = claims.ZoneOf(1, 1);
            Assert.AreEqual(1 * 8 + 1, zone, "named by its smallest plot index");
            Assert.AreEqual(zone, claims.ZoneOf(2, 1));
            Assert.AreEqual(zone, claims.ZoneOf(2, 2), "joined through (2, 1)");
            Assert.AreNotEqual(zone, claims.ZoneOf(3, 3), "only a corner touches (2, 2)");
            Assert.AreNotEqual(zone, claims.ZoneOf(1, 2), "Bob's plot beside hers is another zone");
            Assert.AreEqual(-1, claims.ZoneOf(0, 0), "a free plot has none");
            Assert.AreEqual(-1, claims.ZoneOf(-1, 0));
        }

        [Test]
        public void ReleasingThePlotBetween_SplitsAZone()
        {
            Claims claims = Claims.ForWorld(64, 64);
            claims.TryClaim(Alice, 1, 1, out _);
            claims.TryClaim(Alice, 2, 1, out _);
            claims.TryClaim(Alice, 3, 1, out _);
            Assert.AreEqual(claims.ZoneOf(1, 1), claims.ZoneOf(3, 1));

            claims.TryRelease(Alice, 2, 1, out _);
            Assert.AreNotEqual(claims.ZoneOf(1, 1), claims.ZoneOf(3, 1));
            Assert.AreEqual(-1, claims.ZoneOf(2, 1));
        }

        [Test]
        public void ADamagedSave_IsRefused()
        {
            var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Throws<InvalidDataException>(() => Claims.Read(stream));
        }
    }
}
