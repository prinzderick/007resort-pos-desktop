using R007.Pos.Core.Api;
using R007.Pos.Core.Http;
using R007.Pos.Core.Mock;

namespace R007.Pos.Tests;

/// <summary>A controllable clock for token expiry / age tests.</summary>
public sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>The real client stack (retry, auth, connectivity) wired to the in-memory mock server.</summary>
public sealed class TestEnv : IDisposable
{
    public TestEnv(MockOptions? mock = null, RetryOptions? retry = null)
    {
        Time = new ManualTimeProvider();
        Server = new MockApiHandler(Time, mock ?? new MockOptions());
        Auth = new AuthState();
        Connectivity = new ConnectivityMonitor();
        Http = PosHttp.CreateClient(
            Server,
            Auth,
            Connectivity,
            Endpoint,
            TimeSpan.FromSeconds(5),
            retry ?? new RetryOptions { MaxRetries = 3, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2) },
            Time,
            (_, _) => Task.CompletedTask);
        Api = new R007ApiClient(Http);
    }

    public ServerEndpoint Endpoint { get; } = new(new Uri("http://mock.local/"));

    public ManualTimeProvider Time { get; }

    public MockApiHandler Server { get; }

    public AuthState Auth { get; }

    public ConnectivityMonitor Connectivity { get; }

    public HttpClient Http { get; }

    public R007ApiClient Api { get; }

    public Guid FacilityId { get; private set; }

    /// <summary>Registers this device (as the given facility profile) and signs in with PIN.</summary>
    public async Task<AuthResult> SignInAsync(string code = "RESTAURANT", string staffNumber = "S-1001", string pin = MockData.CashierPin)
    {
        var reg = await Api.RegisterDeviceAsync(new DeviceRegisterRequest("Test POS", DeviceKinds.PosTerminal, "hw-1", code), IdempotencyKeys.New());
        Auth.SetDeviceToken(reg.DeviceToken);
        FacilityId = reg.Device.FacilityId!.Value;
        var login = await Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, staffNumber, pin));
        Auth.SignIn(login, Time.GetUtcNow());
        return login;
    }

    public void Dispose() => Http.Dispose();
}
