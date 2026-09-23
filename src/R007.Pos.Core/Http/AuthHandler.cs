using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Http;

/// <summary>
/// Adds <c>X-Device-Token</c> and <c>Authorization: Bearer</c>, and on a 401 refreshes the staff token once
/// (single flight) and replays the request. If refresh fails the session is expired and the 401 is surfaced.
/// </summary>
public sealed class AuthHandler(AuthState auth, TimeProvider time) : DelegatingHandler
{
    public const string DeviceTokenHeader = "X-Device-Token";

    private static readonly string[] AnonymousPaths = ["/auth/staff/login", "/auth/staff/refresh", "/auth/staff/step-up", "/devices/register", "/health/"];

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var anonymous = IsAnonymous(request);
        var sentToken = auth.AccessToken;

        using var attempt = await RequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
        Decorate(attempt, anonymous);
        var response = await base.SendAsync(attempt, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized || anonymous || auth.RefreshToken is null)
        {
            return response;
        }

        response.Dispose();
        if (!await RefreshAsync(request, sentToken, cancellationToken).ConfigureAwait(false))
        {
            using var again = await RequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
            Decorate(again, anonymous);
            return await base.SendAsync(again, cancellationToken).ConfigureAwait(false);
        }

        using var retry = await RequestCloner.CloneAsync(request, cancellationToken).ConfigureAwait(false);
        Decorate(retry, anonymous);
        return await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAnonymous(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        return AnonymousPaths.Any(p => path.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private void Decorate(HttpRequestMessage request, bool anonymous)
    {
        request.Headers.Remove(DeviceTokenHeader);
        if (auth.DeviceToken is { Length: > 0 } device)
        {
            request.Headers.TryAddWithoutValidation(DeviceTokenHeader, device);
        }

        if (!anonymous && auth.AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Returns true if a fresh token is now available (either we refreshed or another caller already did).</summary>
    private async Task<bool> RefreshAsync(HttpRequestMessage original, string? tokenThatFailed, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (auth.AccessToken is not null && auth.AccessToken != tokenThatFailed)
            {
                return true; // another request already refreshed
            }

            var refreshToken = auth.RefreshToken;
            if (refreshToken is null || original.RequestUri is null)
            {
                return false;
            }

            var uri = RefreshUri(original.RequestUri);
            using var refresh = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new RefreshRequest(refreshToken), PosJsonContext.Default.RefreshRequest)),
            };
            refresh.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Decorate(refresh, anonymous: true);

            using var response = await base.SendAsync(refresh, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                auth.ExpireSession();
                return false;
            }

            var login = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), PosJsonContext.Default.AuthResult);
            if (login is null)
            {
                auth.ExpireSession();
                return false;
            }

            auth.UpdateTokens(login, time.GetUtcNow());
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static Uri RefreshUri(Uri requestUri)
    {
        var abs = requestUri.AbsoluteUri;
        var idx = abs.IndexOf("/api/v1/", StringComparison.OrdinalIgnoreCase);
        var prefix = idx >= 0 ? abs[..idx] : requestUri.GetLeftPart(UriPartial.Authority);
        return new Uri($"{prefix}/api/v1/auth/staff/refresh");
    }
}
