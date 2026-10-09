using System.Net;
using System.Text;
using R007.Pos.Core;
using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Http;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Security;
using R007.Pos.Core.Terminal;
using R007.Pos.Devices.Simulated;
using R007.Pos.ViewModels;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.Tests;

/// <summary>
/// Enrolling a terminal on the ONLINE (cloud) server: replays the cloud's real replies (captured from api.007resorts.com)
/// through the whole registration step, with the real encrypted identity store.
/// </summary>
public sealed class CloudRegistrationTests : IDisposable
{
    private const string FacilityId = "a206b41c-f916-5185-b405-db8a5b67b4db";

    // Exactly what POST /api/v1/devices/register answered on the cloud (token replaced).
    private const string RegisterReply = """
        {
          "device": {
            "id": "01a12051-557c-71dd-8a3d-d3c45dfbe512",
            "name": "Till 1",
            "kind": "POS_TERMINAL",
            "mode": "POS",
            "status": "ACTIVE",
            "facilityId": null,
            "homeFacilityId": "a206b41c-f916-5185-b405-db8a5b67b4db",
            "operatingPointId": null,
            "homeFacility": { "id": "a206b41c-f916-5185-b405-db8a5b67b4db", "code": "RESTAURANT", "name": "Restaurant", "kind": "RESTAURANT" },
            "platform": "windows",
            "appVersion": "0.1.0",
            "lastSeenAt": null,
            "checkout": null,
            "rowVersion": 1
          },
          "deviceToken": "device-token-for-test"
        }
        """;

    private const string NotFoundProblem = """
        {"type":"urn:r007:problem:not_found","title":"Not found","status":404,"code":"not_found","detail":"The requested resource was not found.","instance":"/api/v1/facilities/a206b41c-f916-5185-b405-db8a5b67b4db","correlationId":"01a12056"}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r007-cloudreg-" + Guid.NewGuid().ToString("N"));

    public CloudRegistrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task Registering_OnTheCloud_WithTheClouds_RealReplies_Succeeds_AndSavesTheEnrolment()
    {
        var (ctx, store, calls, dispose) = Build(RegisterReply);
        using var _ = dispose;

        await ctx.RegisterAsync(new Uri("https://api.example.test/"), "R7-AAAAA-BBBBB", "Till 1");

        Assert.NotNull(ctx.Identity);
        Assert.Equal(Guid.Parse(FacilityId), ctx.Identity!.FacilityUnitId);
        Assert.NotNull(store.Load());
        Assert.Contains("POST /api/v1/devices/register", calls);
    }

    [Fact]
    public async Task Registering_WithACodeThatHadNoFacility_SaysWhatToDo_InsteadOfSomethingWentWrong()
    {
        // A code made in the admin without choosing a Home facility registers the device but attaches it to nothing.
        var noFacility = RegisterReply.Replace("\"homeFacilityId\": \"" + FacilityId + "\"", "\"homeFacilityId\": null", StringComparison.Ordinal);
        var (ctx, store, _, dispose) = Build(noFacility);
        using var _ = dispose;

        var failure = await Assert.ThrowsAsync<OperatorException>(() => ctx.RegisterAsync(new Uri("https://api.example.test/"), "R7-AAAAA-BBBBB", "Till 1"));

        var shown = ScreenViewModel.Describe(failure);
        Assert.Contains("Home facility", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("Something went wrong", shown, StringComparison.Ordinal);
        Assert.Null(store.Load());
    }

    private (PosContext Ctx, FileDeviceIdentityStore Store, List<string> Calls, IDisposable Dispose) Build(string registerReply)
    {
        var calls = new List<string>();
        var handler = new Replay(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath == "/api/v1/devices/register"
                ? Json(HttpStatusCode.Created, registerReply, "application/json")
                : Json(HttpStatusCode.NotFound, NotFoundProblem, "application/problem+json");
        });
        var time = new ManualTimeProvider();
        var auth = new AuthState();
        var connectivity = new ConnectivityMonitor();
        var endpoint = new ServerEndpoint(new Uri("http://192.168.1.75/"));
        var http = PosHttp.CreateClient(handler, auth, connectivity, endpoint, TimeSpan.FromSeconds(5),
            new RetryOptions { MaxRetries = 3, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2) }, time, (_, _) => Task.CompletedTask);
        var api = new R007ApiClient(http);
        var queue = new EncryptedFileOfflineQueue(Path.Combine(_dir, "queue-" + Guid.NewGuid().ToString("N") + ".bin"), new InsecureKeyProtector(), time, new OfflineQueueOptions { MaxEntries = 20, MaxAge = TimeSpan.FromMinutes(30) });
        var store = new FileDeviceIdentityStore(Path.Combine(_dir, "identity-" + Guid.NewGuid().ToString("N") + ".bin"), new InsecureKeyProtector());
        var ctx = new PosContext(api, auth, connectivity, endpoint, queue, new EmergencyQueue(queue, auth, time), new QueueReplayService(queue, http, auth),
            new SimulatedReceiptPrinter(), new SimulatedCashDrawer(), store, new PosOptions(), time, "hw-test", "0.1.0", (_, _) => Task.CompletedTask, null);
        return (ctx, store, calls, new Cleanup(queue, http));
    }

    private sealed class Cleanup(EncryptedFileOfflineQueue queue, HttpClient http) : IDisposable
    {
        public void Dispose()
        {
            queue.Dispose();
            http.Dispose();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string contentType) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private sealed class Replay(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
