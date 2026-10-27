using RecurringIntegrationsScheduler.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Tests.TestCommon.Mocks
{
    /// <summary>
    /// Polling clock with simulated time. Delays complete immediately and move the clock forward.
    /// </summary>
    public class FakePollingClock : IPollingClock
    {
        public FakePollingClock(DateTime? startUtc = null)
        {
            UtcNow = startUtc ?? new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
        }

        public DateTime UtcNow { get; set; }

        /// <summary>
        /// Requested delays in order
        /// </summary>
        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

        /// <summary>
        /// Optional action invoked on each delay (e.g. to cancel a token)
        /// </summary>
        public Action<TimeSpan> OnDelay { get; set; }

        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            OnDelay?.Invoke(delay);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
