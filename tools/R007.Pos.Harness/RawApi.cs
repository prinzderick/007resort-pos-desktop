using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace R007.Pos.Harness;

public sealed record RawResponse(HttpStatusCode Status, JsonNode? Body, HttpResponseHeaders Headers, string Text)
{
    public int Code => (int)Status;

    public string? ProblemCode => Body?["code"]?.GetValue<string>();

    public string? ETag => Headers.ETag?.Tag;

    public JsonNode Json => Body ?? throw new ScenarioFailure($"Expected a JSON body (HTTP {Code}): {Text}");

    public RawResponse Expect(HttpStatusCode status, string what)
    {
        if (Status != status)
        {
            throw new ScenarioFailure($"{what}: expected HTTP {(int)status} but got {Code}: {Text}");
        }

        return this;
    }

    public RawResponse Ok(string what) => Status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices
        ? this
        : throw new ScenarioFailure($"{what}: HTTP {Code}: {Text}");
}

/// <summary>
/// A deliberately dumb JSON-over-HTTP client, independent of the POS's typed client. Used to (a) act as the *other* people
/// in a scenario (IT admin, supervisor on another device, kitchen screen) and (b) assert what is really on the wire
/// without going through the POS's own DTOs.
/// </summary>
public sealed class RawApi(Uri root)
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(30) };

    public Uri Root { get; } = root;

    public async Task<RawResponse> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? bearer = null,
        string? deviceToken = null,
        string? idempotencyKey = null,
        string? ifMatch = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(Root, "api/v1/" + path.TrimStart('/')));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (deviceToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Device-Token", deviceToken);
        }

        if (idempotencyKey is not null || method != HttpMethod.Get)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey ?? Guid.CreateVersion7().ToString("D"));
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(k, v);
        }

        if (body is not null)
        {
            var text = body is JsonNode node ? node.ToJsonString() : System.Text.Json.JsonSerializer.Serialize(body);
            request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        JsonNode? json = null;
        if (content.Length > 0)
        {
            try
            {
                json = JsonNode.Parse(content);
            }
            catch (System.Text.Json.JsonException)
            {
                // non-JSON body: leave null, Text carries it
            }
        }

        return new RawResponse(response.StatusCode, json, response.Headers, content);
    }

    public Task<RawResponse> GetAsync(string path, string? bearer = null, string? deviceToken = null) =>
        SendAsync(HttpMethod.Get, path, null, bearer, deviceToken);

    public Task<RawResponse> PostAsync(string path, object? body, string? bearer = null, string? deviceToken = null, string? idempotencyKey = null, string? ifMatch = null) =>
        SendAsync(HttpMethod.Post, path, body ?? new JsonObject(), bearer, deviceToken, idempotencyKey, ifMatch);

    /// <summary>Signs a seeded staff member in (PIN) and returns the bearer token.</summary>
    public async Task<string> LoginPinAsync(string staffNumber, string pin = "1234", string? deviceToken = null)
    {
        var response = await PostAsync("auth/staff/login", new JsonObject { ["credentialType"] = "PIN", ["identifier"] = staffNumber, ["secret"] = pin }, deviceToken: deviceToken).ConfigureAwait(false);
        return response.Ok($"login {staffNumber}").Json["accessToken"]!.GetValue<string>();
    }
}
