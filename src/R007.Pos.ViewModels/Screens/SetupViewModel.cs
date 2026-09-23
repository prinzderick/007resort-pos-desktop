using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>Device registration: point this terminal at the site server and enrol it with a one-time registration code.</summary>
public sealed class SetupViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private string _serverUrl;
    private string _registrationCode = string.Empty;
    private string _deviceName;
    private string? _serverStatus;

    public SetupViewModel(PosContext ctx)
    {
        _ctx = ctx;
        _serverUrl = ctx.Endpoint.Current.AbsoluteUri.TrimEnd('/');
        _deviceName = ctx.Options.DeviceRegistration.DeviceName ?? Environment.MachineName;
        TestCommand = new AsyncRelayCommand(TestAsync, () => !IsBusy, SetError);
        RegisterCommand = new AsyncRelayCommand(RegisterAsync, () => !IsBusy, SetError);
    }

    public event EventHandler? Registered;

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string RegistrationCode
    {
        get => _registrationCode;
        set => SetProperty(ref _registrationCode, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    /// <summary>Shown only in demo mode (<c>R007_MOCK=true</c>): the seeded registration codes.</summary>
    public string? DemoHint => _ctx.Options.Mock
        ? "DEMO MODE (built-in mock server). Registration codes: RESTAURANT, CLUB (Indoor Club, NFC + PIN), RECEPTION (sports/pool)."
        : null;

    public string? ServerStatus
    {
        get => _serverStatus;
        private set => SetProperty(ref _serverStatus, value);
    }

    public AsyncRelayCommand TestCommand { get; }

    public AsyncRelayCommand RegisterCommand { get; }

    private bool TryUrl(out Uri url)
    {
        if (Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            url = parsed;
            return true;
        }

        url = _ctx.Endpoint.Current;
        Error = "Enter the server address, e.g. http://192.168.1.10:8080";
        return false;
    }

    private async Task TestAsync()
    {
        if (!TryUrl(out var url))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var previous = _ctx.Endpoint.Current;
            _ctx.Endpoint.Current = url;
            var check = await _ctx.CheckServerAsync().ConfigureAwait(true);
            if (!check.Ok)
            {
                _ctx.Endpoint.Current = previous;
            }

            ServerStatus = check.Message;
            if (!check.Ok)
            {
                Error = check.Message;
            }
        }).ConfigureAwait(true);
    }

    private async Task RegisterAsync()
    {
        if (!TryUrl(out var url))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(RegistrationCode) || string.IsNullOrWhiteSpace(DeviceName))
        {
            Error = "Enter the registration code from IT and a name for this terminal.";
            return;
        }

        var ok = await RunAsync(() => _ctx.RegisterAsync(url, RegistrationCode, DeviceName.Trim())).ConfigureAwait(true);
        if (ok)
        {
            RegistrationCode = string.Empty;
            Registered?.Invoke(this, EventArgs.Empty);
        }
    }
}
