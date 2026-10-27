/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Common.Helpers
{
    /// <summary>
    /// Time source used by jobs waiting for long running D365 operations.
    /// </summary>
    public interface IPollingClock
    {
        /// <summary>
        /// Current time (UTC)
        /// </summary>
        DateTime UtcNow { get; }

        /// <summary>
        /// Waits for the given time
        /// </summary>
        /// <param name="delay">Delay</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task Delay(TimeSpan delay, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Polling clock based on system time
    /// </summary>
    public sealed class SystemPollingClock : IPollingClock
    {
        /// <summary>
        /// Shared instance
        /// </summary>
        public static SystemPollingClock Instance { get; } = new SystemPollingClock();

        private SystemPollingClock()
        {
        }

        /// <inheritdoc />
        public DateTime UtcNow => DateTime.UtcNow;

        /// <inheritdoc />
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }
}
