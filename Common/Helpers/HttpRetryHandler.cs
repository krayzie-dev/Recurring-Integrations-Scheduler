/* Copyright (c) Microsoft Corporation. All rights reserved.
   Licensed under the MIT License. */

using log4net;
using RecurringIntegrationsScheduler.Common.JobSettings;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RecurringIntegrationsScheduler.Common.Helpers
{
    public class HttpRetryHandler : DelegatingHandler
    {
        private int _retryAfter;
        private static readonly HttpStatusCode TooManyRequestsStatusCode = (HttpStatusCode)429;
        private static readonly HttpStatusCode[] TransientStatusCodes =
        {
            HttpStatusCode.RequestTimeout,
            TooManyRequestsStatusCode,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout
        };
        private static readonly RandomNumberGenerator JitterGenerator = RandomNumberGenerator.Create();
        private readonly Settings _settings;
        /// <summary>
        /// The log
        /// </summary>
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public HttpRetryHandler(HttpMessageHandler innerHandler, Settings jobSettings)
            : base(innerHandler)
        {
            _settings = jobSettings;
            _retryAfter = _settings.RetryDelay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = null;
            for (int attempt = 0; attempt < _settings.RetryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (attempt < _settings.RetryCount - 1)
                {
                    Log.Warn($@"Job: {_settings.JobKey}. HttpRetryHandler encountered a transport error (attempt {attempt + 1}/{_settings.RetryCount}). Retrying...", ex);
                    await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _settings.RetryCount - 1)
                {
                    Log.Warn($@"Job: {_settings.JobKey}. HttpRetryHandler detected a timeout (attempt {attempt + 1}/{_settings.RetryCount}). Retrying...");
                    await DelayAsync(attempt, null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response?.IsSuccessStatusCode ?? false)
                {
                    return response;
                }

                if (!ShouldRetry(response, attempt))
                {
                    return response;
                }

                await DelayAsync(attempt, response, cancellationToken).ConfigureAwait(false);
            }
            return response;
        }

        private bool ShouldRetry(HttpResponseMessage response, int attempt)
        {
            if (response == null)
            {
                return false;
            }

            if (attempt >= _settings.RetryCount - 1)
            {
                return false;
            }

            return IsTransientStatusCode(response.StatusCode);
        }

        /// <summary>
        /// Checks whether HTTP status code means a temporary problem worth retrying later
        /// </summary>
        /// <param name="statusCode">Http status code</param>
        /// <returns>True for timeouts, throttling and temporary server errors</returns>
        public static bool IsTransientStatusCode(HttpStatusCode statusCode)
        {
            if (TransientStatusCodes.Contains(statusCode))
            {
                return true;
            }

            // Retry on custom throttling codes as well
            var code = (int)statusCode;
            return code == 429 || code == 599;
        }

        private async Task DelayAsync(int attempt, HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var retryAfterSeconds = GetServerRetryAfterSeconds(response);
            if (retryAfterSeconds <= 0)
            {
                var exponential = Math.Min(_settings.RetryDelay * Math.Pow(2, attempt), 60);
                retryAfterSeconds = (int)Math.Max(exponential, _settings.RetryDelay);
            }

            var jitterMilliseconds = NextJitterMilliseconds();
            var delay = TimeSpan.FromSeconds(retryAfterSeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);

            Log.Warn($@"Job: {_settings.JobKey}. HttpRetryHandler delaying next request for {delay.TotalSeconds:F1} seconds (attempt {attempt + 1}/{_settings.RetryCount}).");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        private int GetServerRetryAfterSeconds(HttpResponseMessage response)
        {
            if (response?.Headers?.RetryAfter == null)
            {
                return 0;
            }

            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                return (int)Math.Max(response.Headers.RetryAfter.Delta.Value.TotalSeconds, 0);
            }

            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                return (int)Math.Max(delta.TotalSeconds, 0);
            }

            if (response.Headers.Contains("Retry-After"))
            {
                var raw = response.Headers.GetValues("Retry-After").FirstOrDefault();
                if (int.TryParse(raw, out int parsed))
                {
                    return Math.Max(parsed, 0);
                }
            }

            return 0;
        }

        private static int NextJitterMilliseconds()
        {
            var buffer = new byte[4];
            JitterGenerator.GetBytes(buffer);
            var value = BitConverter.ToInt32(buffer, 0);
            return Math.Abs(value % 250);
        }
    }

}
