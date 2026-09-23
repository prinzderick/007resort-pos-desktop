using System.Diagnostics;
using System.Text.Json.Nodes;

namespace R007.Pos.Harness;

/// <summary>Where the real node is and how to talk to it as "everyone else" (IT admin, supervisors, kitchen).</summary>
public sealed class NodeContext
{
    private string? _itAdminToken;
    private string? _ownerToken;

    public NodeContext(Uri baseUrl, string? nodeScript, Action<string>? log = null)
    {
        // Accept both http://host:8080 and http://host:8080/api/v1 (the form used in LOCAL_NODE.md).
        var text = baseUrl.AbsoluteUri.TrimEnd('/');
        if (text.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^"/api/v1".Length];
        }

        Root = new Uri(text + "/");
        NodeScript = nodeScript;
        Log = log ?? (_ => { });
        Raw = new RawApi(Root);
    }

    public Uri Root { get; }

    /// <summary>Path to <c>scripts/local-node.sh</c> (only needed by the outage scenario).</summary>
    public string? NodeScript { get; }

    public Action<string> Log { get; }

    public RawApi Raw { get; }

    public static NodeContext? FromEnvironment(Action<string>? log = null)
    {
        var url = Environment.GetEnvironmentVariable("R007_API_BASE_URL");
        return string.IsNullOrWhiteSpace(url) ? null : new NodeContext(new Uri(url), Environment.GetEnvironmentVariable("R007_NODE_SCRIPT"), log);
    }

    public async Task<string> ItAdminAsync() => _itAdminToken ??= await Raw.LoginPinAsync("S-0012").ConfigureAwait(false);

    /// <summary>owner1 holds every permission (staff.manage, audit.view, ...).</summary>
    public async Task<string> OwnerAsync() => _ownerToken ??= await Raw.LoginPinAsync("S-0013").ConfigureAwait(false);

    public async Task<Guid> FacilityIdAsync(string code)
    {
        var token = await ItAdminAsync().ConfigureAwait(false);
        var list = (await Raw.GetAsync("organization/facilities?limit=200", token).ConfigureAwait(false)).Ok("facilities").Json["items"]!.AsArray();
        var hit = list.FirstOrDefault(f => f?["code"]?.GetValue<string>() == code) ?? throw new ScenarioFailure($"No facility with code {code}");
        return Guid.Parse(hit["id"]!.GetValue<string>());
    }

    /// <summary>A fresh one-time registration code (POST /devices/registration-codes as IT admin).</summary>
    public async Task<string> NewRegistrationCodeAsync(string facilityCode)
    {
        var facility = await FacilityIdAsync(facilityCode).ConfigureAwait(false);
        var token = await ItAdminAsync().ConfigureAwait(false);
        var response = await Raw.PostAsync("devices/registration-codes", new JsonObject { ["facilityId"] = facility.ToString("D") }, token).ConfigureAwait(false);
        return response.Ok("registration code").Json["code"]!.GetValue<string>();
    }

    /// <summary>Runs <c>local-node.sh stop|start</c>. The node is restarted by the scenario; nothing in the API worktree is edited.</summary>
    public async Task NodeControlAsync(string verb)
    {
        var script = NodeScript ?? throw new ScenarioFailure("R007_NODE_SCRIPT (path to scripts/local-node.sh) is required for the outage scenario.");
        using var process = Process.Start(new ProcessStartInfo("bash", $"\"{script}\" {verb}") { RedirectStandardOutput = true, RedirectStandardError = true }) ?? throw new ScenarioFailure("Could not start bash.");
        await process.WaitForExitAsync().ConfigureAwait(false);
        Log($"local-node.sh {verb}: exit {process.ExitCode}");
    }

    public async Task WaitForNodeAsync(bool up, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            bool reachable;
            try
            {
                reachable = (await Raw.GetAsync("health/live").ConfigureAwait(false)).Code == 200;
            }
            catch (HttpRequestException)
            {
                reachable = false;
            }
            catch (TaskCanceledException)
            {
                reachable = false;
            }

            if (reachable == up)
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new ScenarioFailure($"The node did not become {(up ? "reachable" : "unreachable")} within {timeout.TotalSeconds:0}s.");
    }
}
