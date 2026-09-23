namespace R007.Pos.Core.Http;

/// <summary>
/// Builds the one HTTP pipeline the whole POS uses (real or mock): connectivity reporting (outermost) -> retry with
/// idempotency-aware backoff -> auth (device token, bearer, single-flight refresh) -> transport.
/// The transport is the platform <see cref="HttpClientHandler"/> against the real API, or a
/// <c>MockApiHandler</c> in demo/test mode.
/// </summary>
public static class PosHttp
{
    public static HttpClient CreateClient(
        HttpMessageHandler transport,
        AuthState auth,
        ConnectivityMonitor connectivity,
        Uri baseAddress,
        TimeSpan timeout,
        RetryOptions? retry = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var authHandler = new AuthHandler(auth, time ?? TimeProvider.System) { InnerHandler = transport };
        var retryHandler = new RetryHandler(retry ?? new RetryOptions(), delay) { InnerHandler = authHandler };
        var connectivityHandler = new ConnectivityHandler(connectivity) { InnerHandler = retryHandler };

        var normalized = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
        return new HttpClient(connectivityHandler, disposeHandler: true) { BaseAddress = normalized, Timeout = timeout };
    }
}
