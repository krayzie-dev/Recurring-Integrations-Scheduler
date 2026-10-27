/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using System;

namespace RecurringIntegrationsScheduler.Common.Helpers
{
    /// <summary>
    /// Calculates growing delays between status checks of long running D365 operations.
    /// </summary>
    public class StatusCheckBackoff
    {
        /// <summary>
        /// Initial delay used when job does not define one
        /// </summary>
        public const int DefaultInitialDelaySeconds = 5;

        /// <summary>
        /// Maximum delay used when job does not define one
        /// </summary>
        public const int DefaultMaxDelaySeconds = 300;

        /// <summary>
        /// Growth factor applied to the delay after each check
        /// </summary>
        public const double GrowthFactor = 1.5;

        /// <summary>
        /// Time after which unfinished execution is reported as an error, when job does not define one (12 hours)
        /// </summary>
        public const int DefaultStatusCheckTimeoutMinutes = 720;

        /// <summary>
        /// Time to wait for import target error keys file, when job does not define one
        /// </summary>
        public const int DefaultErrorKeysFileTimeoutMinutes = 30;

        private readonly TimeSpan _maxDelay;
        private TimeSpan _nextDelay;

        /// <summary>
        /// Initializes a new instance of the <see cref="StatusCheckBackoff"/> class.
        /// </summary>
        /// <param name="initialDelaySeconds">Initial delay in seconds. Values lower than 1 fall back to the default.</param>
        /// <param name="maxDelaySeconds">Maximum delay in seconds. Values lower than 1 fall back to the default.</param>
        public StatusCheckBackoff(int initialDelaySeconds, int maxDelaySeconds)
        {
            var initial = initialDelaySeconds > 0 ? initialDelaySeconds : DefaultInitialDelaySeconds;
            var max = maxDelaySeconds > 0 ? maxDelaySeconds : DefaultMaxDelaySeconds;

            _maxDelay = TimeSpan.FromSeconds(Math.Max(initial, max));
            _nextDelay = TimeSpan.FromSeconds(initial);
        }

        /// <summary>
        /// Returns delay before the next check and increases the following one.
        /// </summary>
        /// <returns>Delay before the next check</returns>
        public TimeSpan Next()
        {
            var current = _nextDelay;
            var grown = TimeSpan.FromTicks((long)(_nextDelay.Ticks * GrowthFactor));
            _nextDelay = grown > _maxDelay ? _maxDelay : grown;
            return current;
        }
    }
}
