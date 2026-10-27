using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace RIS.D365;

/// <summary>
/// Retry of single D365 HTTP requests (transient errors only).
/// </summary>
public sealed class D365HttpOptions
{
    /// <summary>Number of retries of a single request. 4.x: RetryCount.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Base delay (exponential with jitter); Retry-After from D365 wins. 4.x: RetryDelay.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout of the whole request including retries (large package downloads).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(60);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRisD365(this IServiceCollection services, Action<D365HttpOptions>? configure = null)
    {
        var options = new D365HttpOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ID365TokenProvider, MsalTokenProvider>();
        services.TryAddSingleton<ID365ClientFactory, D365ClientFactory>();
        services.AddTransient<D365AuthenticationHandler>();
        services.AddTransient<D365LoggingHandler>();

        var http = services.AddHttpClient(D365ClientFactory.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<D365AuthenticationHandler>();
        http.AddResilienceHandler("ris-d365", builder =>
            {
                builder.AddTimeout(options.Timeout);
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = Math.Max(0, options.RetryCount),
                    Delay = options.RetryDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    MaxDelay = TimeSpan.FromMinutes(1),
                    ShouldRetryAfterHeader = true,
                    ShouldHandle = args =>
                    {
                        var request = args.Context.GetRequestMessage() ?? args.Outcome.Result?.RequestMessage;
                        if (request is not null && request.Options.TryGetValue(D365RequestOptions.NoRetry, out var noRetry) && noRetry)
                        {
                            return ValueTask.FromResult(false);
                        }
                        return ValueTask.FromResult(args.Outcome.Result is null
                            ? args.Outcome.Exception is HttpRequestException
                            : D365ApiException.IsTransientStatus(args.Outcome.Result.StatusCode));
                    }
                });
            });
        // Logging is the innermost handler, so every attempt (also retried ones) is logged.
        http.AddHttpMessageHandler<D365LoggingHandler>();

        return services;
    }
}
