using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Http;
using R007.Pos.Devices.Nfc;
using R007.Pos.Devices.Scanning;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.ViewModels;

public enum Stage
{
    Starting,
    Setup,
    Login,
    Main,
}

/// <summary>
/// Root view model: decides Setup / Login / Main, hosts dialogs, routes scanner + NFC input to the right screen, shows
/// connectivity and the emergency queue, replays the queue when the server comes back, and locks the till when idle.
/// </summary>
public sealed class ShellViewModel : ObservableObject, INavigator
{
    private readonly PosContext _ctx;
    private readonly SynchronizationContext? _sync = SynchronizationContext.Current;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private Stage _stage = Stage.Starting;
    private ScreenViewModel? _current;
    private int _pendingCount;
    private DateTimeOffset _lastActivity;
    private string? _banner;

    public ShellViewModel(PosContext ctx, INfcReader? nfc = null, IBarcodeScanner? scanner = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _ctx = ctx;
        _delay = delay ?? Task.Delay;
        _lastActivity = ctx.Time.GetUtcNow();
        SignOutCommand = new AsyncRelayCommand(SignOutAsync, null, ex => Banner = ScreenViewModel.Describe(ex));
        ctx.Connectivity.Changed += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(ConnectivityText));
            OnPropertyChanged(nameof(IsOffline));
        });
        ctx.Connectivity.Restored += (_, _) => Post(() => _ = DrainQueueAsync());
        ctx.Auth.SessionExpired += (_, _) => Post(() => _ = OnSessionExpiredAsync());
        ctx.Auth.Changed += (_, _) => Post(() =>
        {
            OnPropertyChanged(nameof(StaffName));
            OnPropertyChanged(nameof(IsSignedIn));
        });
        if (nfc is not null)
        {
            nfc.TagRead += (_, e) => Post(() => _ = HandleScanAsync(e.Uid, isCard: true));
        }

        if (scanner is not null)
        {
            scanner.BarcodeScanned += (_, e) => Post(() => _ = HandleScanAsync(e.Data, isCard: false));
        }
    }

    public Stage Stage
    {
        get => _stage;
        private set => SetProperty(ref _stage, value);
    }

    /// <summary>The screen for the current stage (Setup, Login or Main).</summary>
    public ScreenViewModel? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public ObservableCollection<ModalViewModel> Modals { get; } = [];

    public ModalViewModel? TopModal => Modals.Count > 0 ? Modals[^1] : null;

    public bool HasModal => Modals.Count > 0;

    public MainViewModel? Main => Current as MainViewModel;

    public string StaffName => _ctx.Staff?.DisplayName ?? string.Empty;

    public bool IsSignedIn => _ctx.Auth.IsSignedIn;

    public string FacilityName => _ctx.FacilityName;

    public bool IsOffline => _ctx.Connectivity.IsOffline;

    public string ConnectivityText => _ctx.Connectivity.State switch
    {
        ConnectivityState.Online => "Online",
        ConnectivityState.Offline => "OFFLINE: server unreachable",
        _ => "Connecting...",
    };

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (SetProperty(ref _pendingCount, value))
            {
                OnPropertyChanged(nameof(PendingText));
                OnPropertyChanged(nameof(HasPending));
            }
        }
    }

    public bool HasPending => _pendingCount > 0;

    public string PendingText => _pendingCount == 1 ? "1 item waiting to be confirmed" : $"{_pendingCount} items waiting to be confirmed";

    /// <summary>Message across the top (session expired, locked, server too old...).</summary>
    public string? Banner
    {
        get => _banner;
        set
        {
            if (SetProperty(ref _banner, value))
            {
                OnPropertyChanged(nameof(HasBanner));
            }
        }
    }

    public bool HasBanner => !string.IsNullOrEmpty(_banner);

    public AsyncRelayCommand SignOutCommand { get; }

    public async Task StartAsync()
    {
        if (_ctx.LoadIdentity())
        {
            ShowLogin();
            var check = await _ctx.CheckServerAsync().ConfigureAwait(true);
            if (!check.Ok && check.Info is not null)
            {
                Banner = check.Message; // reachable but too old: stop mutating (contract: minClientVersion)
            }
        }
        else
        {
            ShowSetup();
        }

        await RefreshPendingAsync().ConfigureAwait(true);
    }

    private void ShowSetup()
    {
        var setup = new SetupViewModel(_ctx);
        setup.Registered += (_, _) => ShowLogin();
        Stage = Stage.Setup;
        Current = setup;
    }

    private void ShowLogin()
    {
        var login = new LoginViewModel(_ctx);
        login.SignedIn += async (_, _) => await OnSignedInAsync().ConfigureAwait(true);
        Stage = Stage.Login;
        Current = login;
        OnPropertyChanged(nameof(FacilityName));
    }

    private async Task OnSignedInAsync()
    {
        Banner = null;
        _lastActivity = _ctx.Time.GetUtcNow();
        var main = new MainViewModel(_ctx, this);
        Stage = Stage.Main;
        Current = main;
        OnPropertyChanged(nameof(Main));
        await main.InitializeAsync().ConfigureAwait(true);
        _ = DrainQueueAsync();
    }

    private async Task OnSessionExpiredAsync()
    {
        if (Stage != Stage.Main)
        {
            return;
        }

        Modals.Clear();
        NotifyModals();
        ShowLogin();
        Banner = "Your session expired. Please sign in again.";
        await RefreshPendingAsync().ConfigureAwait(true);
    }

    public async Task SignOutAsync()
    {
        Modals.Clear();
        NotifyModals();
        await _ctx.SignOutAsync().ConfigureAwait(true);
        ShowLogin();
    }

    /// <summary>Call on any user input so the idle lock does not fire.</summary>
    public void NotifyActivity() => _lastActivity = _ctx.Time.GetUtcNow();

    // Dialogs ----------------------------------------------------------------------------------------------------
    public async Task<bool> ShowModalAsync(ModalViewModel modal)
    {
        ArgumentNullException.ThrowIfNull(modal);
        Modals.Add(modal);
        NotifyModals();
        modal.CloseRequested += (_, _) => Post(() =>
        {
            Modals.Remove(modal);
            NotifyModals();
        });
        return await modal.Closed.ConfigureAwait(true);
    }

    private void NotifyModals()
    {
        OnPropertyChanged(nameof(TopModal));
        OnPropertyChanged(nameof(HasModal));
    }

    // Input routing (keyboard-wedge or dedicated readers) ---------------------------------------------------------
    /// <summary>Text from a keyboard-wedge burst (scanner or NFC reader). On the login screen it is a card; elsewhere a barcode.</summary>
    public Task HandleWedgeInputAsync(string text) => HandleScanAsync(text, isCard: Stage == Stage.Login);

    private async Task HandleScanAsync(string data, bool isCard)
    {
        NotifyActivity();
        try
        {
            if (TopModal is IScanTarget modalTarget)
            {
                await modalTarget.HandleScanAsync(data).ConfigureAwait(true);
            }
            else if (Stage == Stage.Login && isCard && Current is IScanTarget login)
            {
                await login.HandleScanAsync(data).ConfigureAwait(true);
            }
            else if (Stage == Stage.Main && !isCard && Main?.Current is IScanTarget screen)
            {
                await screen.HandleScanAsync(data).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            Banner = ScreenViewModel.Describe(ex);
        }
    }

    // Background: connectivity probe, queue replay, badges, idle lock ---------------------------------------------
    public async Task RunBackgroundAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _ctx.Options.Offline.ProbeIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _delay(interval, ct).ConfigureAwait(true);
                await TickAsync(ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One background tick (public so tests can drive it without timers).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (_ctx.Connectivity.State != ConnectivityState.Online && _ctx.IsRegistered)
        {
            await _ctx.Api.PingAsync(ct).ConfigureAwait(true); // the pipeline reports the result; Restored triggers a drain
        }

        await RefreshPendingAsync().ConfigureAwait(true);
        if (PendingCount > 0 && _ctx.Connectivity.State == ConnectivityState.Online && _ctx.Auth.IsSignedIn)
        {
            await DrainQueueAsync().ConfigureAwait(true);
        }

        if (Stage == Stage.Main && Main is { } main)
        {
            await main.RefreshBadgesAsync().ConfigureAwait(true);
            var idle = _ctx.Options.IdleLockMinutes;
            if (idle > 0 && _ctx.Time.GetUtcNow() - _lastActivity > TimeSpan.FromMinutes(idle) && !HasModal)
            {
                await SignOutAsync().ConfigureAwait(true);
                Banner = "Locked after inactivity. Sign in to continue.";
            }
        }
    }

    private async Task DrainQueueAsync()
    {
        if (!_ctx.Auth.IsSignedIn)
        {
            return;
        }

        try
        {
            await _ctx.Replay.DrainAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // stays queued
        }

        await RefreshPendingAsync().ConfigureAwait(true);
        if (Main?.Queue is { } queue)
        {
            await queue.LoadAsync().ConfigureAwait(true);
        }
    }

    private async Task RefreshPendingAsync()
    {
        PendingCount = await _ctx.Queue.CountPendingAsync().ConfigureAwait(true);
    }

    private void Post(Action action)
    {
        if (_sync is null || SynchronizationContext.Current == _sync)
        {
            action();
        }
        else
        {
            _sync.Post(_ => action(), null);
        }
    }
}
