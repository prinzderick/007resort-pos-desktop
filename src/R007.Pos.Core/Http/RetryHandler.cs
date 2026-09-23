using System.Net;

namespace R007.Pos.Core.Http;

public sealed class RetryOptions
{
    public int MaxRetries { get; set; } = 3;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Polly-style retry with exponential backoff + jitter for transient failures (network errors, timeouts, 408, 429,
/// 502, 503, 504). A mutating request is only retried when it carries an <c>Idempotency-Key</c>, because the API then
/// guarantees a replay returns the original result instead of applying it twice. <c>Retry-After</c> is honoured.
/// </summary>
public sealed class RetryHandler(RetryOptions options, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null) : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Random _random = random ?? Random.Shared;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var retryable = IsSafeToRetry(request);
        var attempt = 0;

        while (true)
        {
            using var clone = await RequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            Exception? failure = null;

            try
            {
                response = await base.SendAsync(clone, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                failure = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                failure = ex; // HttpClient timeout
            }

            var transient = failure is not null || IsTransient(response!.StatusCode);
            if (!transient || !retryable || attempt >= options.MaxRetries)
            {
                if (failure is not null)
                {
                    throw failure is HttpRequestException ? failure : new HttpRequestException("Request timed out.", failure);
                }

                return response!;
            }

            var wait = ComputeDelay(attempt, response);
            response?.Dispose();
            attempt++;
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSafeToRetry(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        || request.Method == HttpMethod.Head
        || request.Headers.Contains("Idempotency-Key");

    private static bool IsTransient(HttpStatusCode code) =>
        code is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < options.MaxDelay ? delta : options.MaxDelay;
        }

        var exp = options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
        var jitter = 0.5 + _random.NextDouble(); // 0.5x .. 1.5x
        var ms = Math.Min(exp * jitter, options.MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }
}
