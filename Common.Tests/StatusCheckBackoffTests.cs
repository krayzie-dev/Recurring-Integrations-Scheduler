using Microsoft.VisualStudio.TestTools.UnitTesting;
using RecurringIntegrationsScheduler.Common.Helpers;
using System;
using System.Linq;

namespace RecurringIntegrationsScheduler.Common.Tests
{
    [TestClass]
    public class StatusCheckBackoffTests
    {
        [TestMethod]
        public void Next_GrowsByFactorUpToMaximum()
        {
            var backoff = new StatusCheckBackoff(5, 300);

            var delays = Enumerable.Range(0, 15).Select(_ => backoff.Next().TotalSeconds).ToArray();

            Assert.AreEqual(5, delays[0], 0.001);
            Assert.AreEqual(7.5, delays[1], 0.001);
            Assert.AreEqual(11.25, delays[2], 0.001);
            for (var i = 1; i < delays.Length; i++)
            {
                Assert.IsTrue(delays[i] >= delays[i - 1], "Delay must not decrease.");
                Assert.IsTrue(delays[i] <= 300, "Delay must not exceed maximum.");
            }
            Assert.AreEqual(300, delays.Last(), 0.001);
        }

        [TestMethod]
        [DataRow(0, 0)]
        [DataRow(-1, -10)]
        public void Constructor_UsesDefaultsForNonPositiveValues(int initial, int max)
        {
            var backoff = new StatusCheckBackoff(initial, max);

            Assert.AreEqual(TimeSpan.FromSeconds(StatusCheckBackoff.DefaultInitialDelaySeconds), backoff.Next());

            TimeSpan last = TimeSpan.Zero;
            for (var i = 0; i < 50; i++)
            {
                last = backoff.Next();
            }
            Assert.AreEqual(TimeSpan.FromSeconds(StatusCheckBackoff.DefaultMaxDelaySeconds), last);
        }

        [TestMethod]
        public void Constructor_MaximumLowerThanInitial_KeepsInitialDelay()
        {
            var backoff = new StatusCheckBackoff(600, 300);

            Assert.AreEqual(TimeSpan.FromSeconds(600), backoff.Next());
            Assert.AreEqual(TimeSpan.FromSeconds(600), backoff.Next());
        }

        [TestMethod]
        public void TwelveHoursOfPolling_NeedsAroundOneHundredFiftyChecks()
        {
            var backoff = new StatusCheckBackoff(5, 300);
            var elapsed = TimeSpan.Zero;
            var checks = 0;

            while (elapsed < TimeSpan.FromHours(12))
            {
                elapsed += backoff.Next();
                checks++;
            }

            Assert.IsTrue(checks > 140 && checks < 160, $"Unexpected number of checks: {checks}");
        }
    }
}
