using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Http;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Realtime;
using R007.Pos.Core.Terminal;
using R007.Pos.Devices.CashDrawer;
using R007.Pos.Devices.Printing;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.ViewModels;

public sealed record ServerCheck(bool Ok, string Message, SystemInfo? Info);

/// <summary>
/// Shared, observable state of this terminal: device identity, facility capabilities (which drive the screens),
/// the signed-in staff member (from <see cref="AuthState"/>), cached catalog and the open cash session. View models
/// receive this one object instead of a dozen services. Nothing here is business logic: it caches what the API said.
/// </summary>
public sealed class PosContext : ObservableObject
{
    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private DeviceIdentity? _identity;
    private FacilityCapabilities? _capabilities;
    private string _facilityName = string.Empty;
    private TerminalFeatures _features = TerminalFeatures.None;
    private CashSession? _cashSession;
    private IReadOnlyList<Category> _categories = [];
    private IReadOnlyList<Product> _products = [];

    public PosContext(
        IR007ApiClient api,
        AuthState auth,
        ConnectivityMonitor connectivity,
        ServerEndpoint endpoint,
        IOfflineQueue queue,
        EmergencyQueue emergency,
        QueueReplayService replay,
        IReceiptPrinter printer,
        ICashDrawer drawer,
        IDeviceIdentityStore identityStore,
        PosOptions options,
        TimeProvider time,
        string hardwareId,
        string appVersion,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<IRealtimeSocket>? realtimeSocketFactory = null)
    {
        Api = api;
        Auth = auth;
        Connectivity = connectivity;
        Endpoint = endpoint;
        Queue = queue;
        Emergency = emergency;
        Replay = replay;
        Printer = printer;
        Drawer = drawer;
        IdentityStore = identityStore;
        Options = options;
        Time = time;
        HardwareId = hardwareId;
        AppVersion = appVersion;
        Printing = new PrintService(api, printer, options.Printer.CharactersPerLine);
        Approvals = new ApprovalCoordinator(api, options.Approvals, time, delay);
        Orders = new OrderWorkflow(api, emergency, time);
        Realtime = new RealtimeClient(api, realtimeSocketFactory ?? (() => new ClientWebSocketAdapter()), delay);
        auth.Changed += (_, _) => RecomputeFeatures();
    }

    public IR007ApiClient Api { get; }

    public AuthState Auth { get; }

    public ConnectivityMonitor Connectivity { get; }

    public ServerEndpoint Endpoint { get; }

    public IOfflineQueue Queue { get; }

    public EmergencyQueue Emergency { get; }

    public QueueReplayService Replay { get; }

    public IReceiptPrinter Printer { get; }

    public ICashDrawer Drawer { get; }

    public IDeviceIdentityStore IdentityStore { get; }

    public PosOptions Options { get; }

    public TimeProvider Time { get; }

    public string HardwareId { get; }

    public string AppVersion { get; }

    public PrintService Printing { get; }

    public ApprovalCoordinator Approvals { get; }

    public OrderWorkflow Orders { get; }

    /// <summary>Push hints (approval decided/requested, device commands). Polling remains the source of truth.</summary>
    public RealtimeClient Realtime { get; }

    /// <summary>Last successful <c>GET /system/info</c> (realtime endpoint, versions).</summary>
    public SystemInfo? ServerInfo { get; private set; }

    public DeviceIdentity? Identity
    {
        get => _identity;
        private set
        {
            if (SetProperty(ref _identity, value))
            {
                OnPropertyChanged(nameof(IsRegistered));
            }
        }
    }

    public bool IsRegistered => _identity is not null;

    public FacilityCapabilities? Capabilities
    {
        get => _capabilities;
        private set => SetProperty(ref _capabilities, value);
    }

    public string FacilityName
    {
        get => _facilityName;
        private set => SetProperty(ref _facilityName, value);
    }

    public TerminalFeatures Features
    {
        get => _features;
        private set => SetProperty(ref _features, value);
    }

    public CashSession? CashSession
    {
        get => _cashSession;
        set
        {
            if (SetProperty(ref _cashSession, value))
            {
                OnPropertyChanged(nameof(HasOpenCashSession));
            }
        }
    }

    public bool HasOpenCashSession => _cashSession is { IsOpen: true };

    public IReadOnlyList<Category> Categories
    {
        get => _categories;
        private set => SetProperty(ref _categories, value);
    }

    public IReadOnlyList<Product> Products
    {
        get => _products;
        private set => SetProperty(ref _products, value);
    }

    public Guid FacilityId => _identity?.FacilityUnitId ?? throw new InvalidOperationException("This terminal is not registered.");

    public Staff? Staff => Auth.Staff;

    /// <summary>Loads a previously enrolled identity (device token goes into the HTTP pipeline). Returns false if none.</summary>
    public bool LoadIdentity()
    {
        var identity = IdentityStore.Load();
        if (identity is null)
        {
            return false;
        }

        ApplyIdentity(identity);
        return true;
    }

    private void ApplyIdentity(DeviceIdentity identity)
    {
        Identity = identity;
        Endpoint.Current = identity.ServerUrl;
        Auth.SetDeviceToken(identity.DeviceToken);
        FacilityName = identity.FacilityName ?? identity.Name;
    }

    /// <summary>Checks the server is reachable and this build meets its <c>minClientVersion</c>.</summary>
    public async Task<ServerCheck> CheckServerAsync(CancellationToken ct = default)
    {
        try
        {
            var info = await Api.GetSystemInfoAsync(ct).ConfigureAwait(true);
            ServerInfo = info;
            if (info.MinClientVersion is { } min
                && (min.TryGetValue("POS_TERMINAL", out var required) || min.TryGetValue("pos", out required))
                && Version.TryParse(required, out var need)
                && Version.TryParse(AppVersion, out var have)
                && have < need)
            {
                return new ServerCheck(false, $"Update required: this POS is {AppVersion}, the server needs {required} or newer.", info);
            }

            return new ServerCheck(true, $"Connected to {info.Service} {info.ApiVersion} ({info.DeploymentMode}).", info);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return new ServerCheck(false, ScreenViewModel.Describe(ex), null);
        }
    }

    /// <summary>Enrols this terminal: registration code -> device token, stored DPAPI-protected. Points the client at <paramref name="serverUrl"/>.</summary>
    public async Task RegisterAsync(Uri serverUrl, string registrationCode, string deviceName, CancellationToken ct = default)
    {
        var previous = Endpoint.Current;
        Endpoint.Current = serverUrl;
        try
        {
            var result = await Api.RegisterDeviceAsync(
                new DeviceRegisterRequest(deviceName, DeviceKinds.PosTerminal, HardwareId, registrationCode.Trim(), OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", AppVersion, DeviceKinds.PosMode),
                IdempotencyKeys.New(),
                ct).ConfigureAwait(true);

            var facilityId = result.Device.FacilityId ?? result.Device.HomeFacilityId
                ?? throw new InvalidOperationException("The server did not assign this terminal to a facility. Ask IT to assign one.");

            string? facilityName = null;
            Auth.SetDeviceToken(result.DeviceToken);
            try
            {
                facilityName = (await Api.GetFacilityAsync(facilityId, ct).ConfigureAwait(true)).Name;
            }
            catch (ApiException)
            {
                // Facility name is cosmetic (needs a permission the device may lack): fall back to the device name.
            }

            var identity = new DeviceIdentity(serverUrl, result.Device.Id, result.DeviceToken, result.Device.Name, facilityId, null, facilityName);
            IdentityStore.Save(identity);
            ApplyIdentity(identity);
        }
        catch
        {
            Endpoint.Current = previous;
            throw;
        }
    }

    /// <summary>Forgets the enrolment (IT re-provisioning). Emergency-queue contents are left untouched.</summary>
    public void ForgetDevice()
    {
        IdentityStore.Clear();
        Auth.SetDeviceToken(null);
        Auth.SignOut();
        Identity = null;
        Capabilities = null;
        RecomputeFeatures();
    }

    public async Task LoadFacilityAsync(CancellationToken ct = default)
    {
        var caps = await Api.GetCapabilitiesAsync(FacilityId, ct).ConfigureAwait(true);
        Capabilities = caps;
        var rules = caps.OperatingRules;
        Emergency.Policy = new OfflinePolicy(rules?.AllowOfflineOrders == true, rules?.AllowOfflinePayments ?? OfflinePaymentPolicy.None);
        RecomputeFeatures();
    }

    public async Task LoadCatalogAsync(CancellationToken ct = default)
    {
        var categories = await Api.GetCategoriesAsync(ct).ConfigureAwait(true);
        var products = await Api.GetProductsAsync(FacilityId, null, ct).ConfigureAwait(true);
        Categories = [.. categories.OrderBy(c => c.SortOrder ?? int.MaxValue).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        Products = [.. products.Where(p => p.Active)];
    }

    public async Task RefreshCashSessionAsync(CancellationToken ct = default)
    {
        if (Staff is null || !Features.CanManageCashSession)
        {
            CashSession = null;
            return;
        }

        CashSession = await Api.GetOpenCashSessionAsync(FacilityId, Staff.Id, ct).ConfigureAwait(true);
    }

    public async Task SignOutAsync()
    {
        try
        {
            if (Auth.IsSignedIn)
            {
                await Api.LogoutAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // The local session is dropped regardless; the server session simply expires.
        }
        finally
        {
            Auth.SignOut();
            CashSession = null;
        }
    }

    public void RecomputeFeatures() =>
        Features = TerminalFeatures.From(Capabilities, Auth.Staff, Options.RequireNfcAndPin);

    /// <summary>
    /// State here is shared by every screen, and some changes originate on pool threads (a token refresh inside the HTTP
    /// pipeline updates <see cref="AuthState"/>). Screens rebuild collections and raise command state in response, which WPF
    /// only allows on the UI thread, so change notifications are marshalled to the context this object was created on.
    /// </summary>
    protected override void OnPropertyChanged(string? propertyName = null)
    {
        if (_sync is null || SynchronizationContext.Current == _sync)
        {
            base.OnPropertyChanged(propertyName);
        }
        else
        {
            _sync.Post(_ => base.OnPropertyChanged(propertyName), null);
        }
    }
}
