using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Http;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Security;
using R007.Pos.Core.Terminal;
using R007.Pos.Devices.Simulated;
using R007.Pos.ViewModels;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

/// <summary>Drives modals from a script instead of a window.</summary>
public sealed class ScriptedNavigator : INavigator
{
    public List<ModalViewModel> Shown { get; } = [];

    public Func<ModalViewModel, Task>? Script { get; set; }

    public async Task<bool> ShowModalAsync(ModalViewModel modal)
    {
        Shown.Add(modal);
        if (Script is not null)
        {
            await Script(modal);
        }

        return modal.Closed.IsCompleted && await modal.Closed;
    }
}

/// <summary>Everything a view-model test needs: real client stack over the mock server, encrypted temp queue, simulated printer.</summary>
public sealed class TestPos : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r007-tests-" + Guid.NewGuid().ToString("N"));

    public TestPos(PosOptions? options = null, RetryOptions? retry = null)
    {
        Directory.CreateDirectory(_dir);
        Env = new TestEnv(retry: retry);
        Options = options ?? new PosOptions();
        Queue = new EncryptedFileOfflineQueue(Path.Combine(_dir, "queue.bin"), new InsecureKeyProtector(), Env.Time, new OfflineQueueOptions { MaxEntries = 20, MaxAge = TimeSpan.FromMinutes(30) });
        Emergency = new EmergencyQueue(Queue, Env.Auth, Env.Time);
        Replay = new QueueReplayService(Queue, Env.Http, Env.Auth);
        Printer = new SimulatedReceiptPrinter();
        Identity = new InMemoryDeviceIdentityStore();
        Navigator = new ScriptedNavigator();
        Ctx = new PosContext(Env.Api, Env.Auth, Env.Connectivity, Env.Endpoint, Queue, Emergency, Replay, Printer, new SimulatedCashDrawer(), Identity, Options, Env.Time, "hw-test", "0.1.0", (_, _) =>
        {
            Env.Time.Advance(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        });
    }

    public TestEnv Env { get; }

    public PosOptions Options { get; }

    public EncryptedFileOfflineQueue Queue { get; }

    public EmergencyQueue Emergency { get; }

    public QueueReplayService Replay { get; }

    public SimulatedReceiptPrinter Printer { get; }

    public InMemoryDeviceIdentityStore Identity { get; }

    public ScriptedNavigator Navigator { get; }

    public PosContext Ctx { get; }

    public MockApiHandler Server => Env.Server;

    /// <summary>Register as the facility profile, sign in via PIN, load facility + catalog (as MainViewModel would).</summary>
    public async Task<PosContext> SignInAsync(string code = "RESTAURANT", string staffNumber = "S-1001", string pin = MockData.CashierPin)
    {
        await Ctx.RegisterAsync(new Uri("http://mock.local/"), code, "Test POS");
        var login = await Env.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, staffNumber, pin));
        Env.Auth.SignIn(login, Env.Time.GetUtcNow());
        await Ctx.LoadFacilityAsync();
        await Ctx.LoadCatalogAsync();
        await Ctx.RefreshCashSessionAsync();
        return Ctx;
    }

    public SellViewModel NewSell() => new(Ctx, Navigator);

    public Product Product(Guid id) => Ctx.Products.First(p => p.Id == id);

    public void Dispose()
    {
        Queue.Dispose();
        Env.Dispose();
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
