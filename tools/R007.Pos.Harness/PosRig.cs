using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Http;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Security;
using R007.Pos.Core.Terminal;
using R007.Pos.Devices.Simulated;
using R007.Pos.ViewModels;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>Drives modals from a script instead of a window (same idea as the unit tests' navigator).</summary>
public sealed class ScriptedNavigator : INavigator
{
    public List<ModalViewModel> Shown { get; } = [];

    public Func<ModalViewModel, Task>? Script { get; set; }

    public async Task<bool> ShowModalAsync(ModalViewModel modal)
    {
        Shown.Add(modal);
        if (Script is not null)
        {
            await Script(modal).ConfigureAwait(true);
        }

        return modal.Closed.IsCompleted && await modal.Closed.ConfigureAwait(true);
    }
}

/// <summary>
/// One POS terminal built exactly like the app builds it (HTTP pipeline, typed client, PosContext, encrypted queue,
/// simulated printer) but pointed at a REAL node and enrolled with a fresh registration code.
/// </summary>
public sealed class PosRig : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r007-harness-" + Guid.NewGuid().ToString("N"));

    private PosRig(NodeContext node, PosOptions options, TimeSpan retryBase)
    {
        Node = node;
        Directory.CreateDirectory(_dir);
        Options = options;
        Endpoint = new ServerEndpoint(node.Root);
        Auth = new AuthState();
        Connectivity = new ConnectivityMonitor();
        Http = PosHttp.CreateClient(
            new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromSeconds(3) },
            Auth,
            Connectivity,
            Endpoint,
            TimeSpan.FromSeconds(15),
            new RetryOptions { MaxRetries = 2, BaseDelay = retryBase, MaxDelay = retryBase * 2 });
        Api = new R007ApiClient(Http);
        Queue = new EncryptedFileOfflineQueue(Path.Combine(_dir, "queue.bin"), new InsecureKeyProtector(), TimeProvider.System, new OfflineQueueOptions { MaxEntries = 50, MaxAge = TimeSpan.FromMinutes(30) });
        Emergency = new EmergencyQueue(Queue, Auth, TimeProvider.System);
        Replay = new QueueReplayService(Queue, Http, Auth);
        Printer = new SimulatedReceiptPrinter();
        Navigator = new ScriptedNavigator();
        Ctx = new PosContext(Api, Auth, Connectivity, Endpoint, Queue, Emergency, Replay, Printer, new SimulatedCashDrawer(), new InMemoryDeviceIdentityStore(), options, TimeProvider.System, "harness-" + Guid.NewGuid().ToString("N")[..8], "0.1.0");
    }

    public NodeContext Node { get; }

    /// <summary>The encrypted emergency-queue file.</summary>
    public string QueueFile => Path.Combine(_dir, "queue.bin");

    public PosOptions Options { get; }

    public ServerEndpoint Endpoint { get; }

    public AuthState Auth { get; }

    public ConnectivityMonitor Connectivity { get; }

    public HttpClient Http { get; }

    public R007ApiClient Api { get; }

    public EncryptedFileOfflineQueue Queue { get; }

    public EmergencyQueue Emergency { get; }

    public QueueReplayService Replay { get; }

    public SimulatedReceiptPrinter Printer { get; }

    public ScriptedNavigator Navigator { get; }

    public PosContext Ctx { get; }

    public string DeviceToken => Auth.DeviceToken ?? throw new ScenarioFailure("not enrolled");

    /// <summary>Enrols a new terminal for the facility (fresh one-time code, mode POS) through the real <c>PosContext.RegisterAsync</c>.</summary>
    public static async Task<PosRig> EnrolAsync(NodeContext node, string facilityCode, PosOptions? options = null, TimeSpan? retryBase = null)
    {
        var rig = new PosRig(node, options ?? new PosOptions { Approvals = new ApprovalOptions { PollIntervalSeconds = 1, WaitTimeoutMinutes = 2 } }, retryBase ?? TimeSpan.FromMilliseconds(100));
        var code = await node.NewRegistrationCodeAsync(facilityCode).ConfigureAwait(false);
        await Throttle.RetryVoidAsync(() => rig.Ctx.RegisterAsync(node.Root, code, $"Harness {facilityCode} {DateTime.UtcNow:HHmmss}")).ConfigureAwait(false);
        return rig;
    }

    /// <summary>Signs in through the real login view-model (staff number + PIN pad) and loads facility, catalog and cash session.</summary>
    public async Task SignInAsync(string staffNumber, string pin = "1234")
    {
        var login = new LoginViewModel(Ctx) { StaffNumber = staffNumber };
        foreach (var digit in pin)
        {
            login.KeyCommand.Execute(digit.ToString());
        }

        await login.SignInCommand.ExecuteAsync().ConfigureAwait(false);
        for (var attempt = 0; !Auth.IsSignedIn && login.Error?.Contains("Too many", StringComparison.OrdinalIgnoreCase) == true && attempt < 6; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(13)).ConfigureAwait(false);
            foreach (var digit in pin)
            {
                login.KeyCommand.Execute(digit.ToString());
            }

            login.StaffNumber = staffNumber;
            await login.SignInCommand.ExecuteAsync().ConfigureAwait(false);
        }

        if (!Auth.IsSignedIn)
        {
            throw new ScenarioFailure($"Sign-in as {staffNumber} failed: {login.Error}");
        }

        await LoadAsync().ConfigureAwait(false);
    }

    public async Task LoadAsync()
    {
        await Ctx.LoadFacilityAsync().ConfigureAwait(false);
        await Ctx.LoadCatalogAsync().ConfigureAwait(false);
        await Ctx.RefreshCashSessionAsync().ConfigureAwait(false);
    }

    public SellViewModel NewSell() => new(Ctx, Navigator);

    public Product Product(string sku) =>
        Ctx.Products.FirstOrDefault(p => string.Equals(p.Sku, sku, StringComparison.OrdinalIgnoreCase)) ?? throw new ScenarioFailure($"No product with sku {sku} at this facility");

    public Product ProductNamed(string name) =>
        Ctx.Products.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ScenarioFailure($"No product named like {name} at this facility");

    /// <summary>Makes sure the signed-in cashier has an OPEN cash session on this device: closes a stale one (counting the expected cash) and opens a fresh one.</summary>
    public async Task<CashSession> FreshCashSessionAsync(decimal openingFloat = 1000m)
    {
        await Ctx.RefreshCashSessionAsync().ConfigureAwait(false);
        if (Ctx.CashSession is { } stale)
        {
            await Api.CloseCashSessionAsync(stale.Id, new CloseCashSessionRequest(stale.ExpectedCash ?? stale.OpeningFloat, "harness reset"), IdempotencyKeys.New()).ConfigureAwait(false);
        }

        var session = await Api.OpenCashSessionAsync(new OpenCashSessionRequest(Ctx.FacilityId, openingFloat), IdempotencyKeys.New()).ConfigureAwait(false);
        Ctx.CashSession = session;
        return session;
    }

    public void Dispose()
    {
        Queue.Dispose();
        Http.Dispose();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
