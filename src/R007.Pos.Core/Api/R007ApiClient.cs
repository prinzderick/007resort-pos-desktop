using System.Net.Http.Json;
using System.Text.Json;

namespace R007.Pos.Core.Api;

/// <summary>
/// <see cref="HttpClient"/>-based implementation of <see cref="IR007ApiClient"/>.
/// The <see cref="HttpClient.BaseAddress"/> must point at the site API (e.g. <c>http://localhost:5080/</c>).
/// </summary>
public sealed class R007ApiClient(HttpClient httpClient) : IR007ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient
            .GetAsync(new Uri("api/v1/system/info", UriKind.Relative), cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var info = await response.Content
            .ReadFromJsonAsync<SystemInfo>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return info ?? throw new InvalidOperationException("The API returned an empty system info response.");
    }
}
