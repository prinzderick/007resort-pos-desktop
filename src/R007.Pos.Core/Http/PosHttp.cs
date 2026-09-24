namespace R007.Pos.Core.Http;

/// <summary>
/// Builds the one HTTP pipeline the whole POS uses (real or mock): connectivity reporting (outermost) -> retry with
/// idempotency-aware backoff -> auth (device token, bearer, single-flight refresh) -> endpoint rewrite -> transport.
/// The transport is the platform <see cref="HttpClientHandler"/> against the real API, or a
/// <c>MockApiHandler</c> in demo/test mode.
/// </summary>
public static class PosHttp
{
    public static HttpClient CreateClient(
        HttpMessageHandler transport,
        AuthState auth,
        ConnectivityMonitor connectivity,
        ServerEndpoint endpoint,
        TimeSpan timeout,
        RetryOptions? retry = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var endpointHandler = new EndpointHandler(endpoint) { InnerHandler = transport };
        var authHandler = new AuthHandler(auth, time ?? TimeProvider.System) { InnerHandler = endpointHandler };
        var retryHandler = new RetryHandler(retry ?? new RetryOptions(), delay) { InnerHandler = authHandler };
        var connectivityHandler = new ConnectivityHandler(connectivity) { InnerHandler = retryHandler };

        return new HttpClient(connectivityHandler, disposeHandler: true) { BaseAddress = EndpointHandler.Placeholder, Timeout = timeout };
    }
}
