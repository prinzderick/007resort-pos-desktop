namespace R007.Pos.Core.Http;

/// <summary>Holds the site API base URL, which can change at runtime (device setup screen).</summary>
public sealed class ServerEndpoint(Uri initial)
{
    private Uri _current = Normalize(initial);

    public Uri Current
    {
        get => _current;
        set => _current = Normalize(value);
    }

    private static Uri Normalize(Uri uri) => uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
}

/// <summary>
/// Innermost handler: rewrites the placeholder base address used by <see cref="System.Net.Http.HttpClient"/> (which
/// cannot be changed after the first request) to the currently configured <see cref="ServerEndpoint"/>. Lets the
/// setup screen point an already-built client at a newly entered server URL.
/// </summary>
public sealed class EndpointHandler(ServerEndpoint endpoint) : DelegatingHandler
{
    public static readonly Uri Placeholder = new("http://pos.invalid/");

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && string.Equals(uri.Host, Placeholder.Host, StringComparison.OrdinalIgnoreCase))
        {
            var relative = uri.PathAndQuery.TrimStart('/');
            request.RequestUri = new Uri(endpoint.Current, relative);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
