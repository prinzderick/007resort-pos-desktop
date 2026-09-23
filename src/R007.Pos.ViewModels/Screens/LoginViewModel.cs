using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

public enum LoginStep
{
    /// <summary>Staff number + PIN, or tap a card (card alone signs in unless this station requires NFC + PIN).</summary>
    Credentials,

    /// <summary>NFC + PIN station: waiting for the card tap.</summary>
    TapCard,

    /// <summary>NFC + PIN station: card read, waiting for the PIN.</summary>
    EnterPin,
}

/// <summary>
/// Staff sign-in with a touch PIN pad. NFC cards arrive as keyboard-wedge text (routed here by the shell). Stations
/// that require NFC + PIN (configuration <c>Pos:RequireNfcAndPin</c>) never sign in on a card alone: the card is proven
/// to the API (<c>NFC_CARD</c>), then the same staff member's PIN is proven (<c>PIN</c>), and only the second session
/// is kept (the provisional card-only session is revoked immediately).
/// </summary>
public sealed class LoginViewModel : ScreenViewModel, IScanTarget
{
    public const int MaxPinLength = 12;

    private readonly PosContext _ctx;
    private string _staffNumber = string.Empty;
    private string _pin = string.Empty;
    private string? _nfcUid;
    private LoginStep _step;
    private bool _usePassword;
    private string _username = string.Empty;
    private string _password = string.Empty;

    public LoginViewModel(PosContext ctx)
    {
        _ctx = ctx;
        _step = ctx.Options.RequireNfcAndPin ? LoginStep.TapCard : LoginStep.Credentials;
        KeyCommand = new RelayCommand(p => PressKey(p as string), _ => !IsBusy);
        BackspaceCommand = new RelayCommand(() => Pin = Pin.Length > 0 ? Pin[..^1] : Pin);
        ClearCommand = new RelayCommand(Reset);
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => CanSignIn, SetError);
        CancelCardCommand = new RelayCommand(Reset);
        TogglePasswordCommand = new RelayCommand(() => UsePassword = !UsePassword, () => !RequiresNfcAndPin);
    }

    /// <summary>Raised after a successful sign-in (session tokens are in <see cref="PosContext.Auth"/>).</summary>
    public event EventHandler? SignedIn;

    public LoginStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                OnPropertyChanged(nameof(Prompt));
                OnPropertyChanged(nameof(ShowStaffNumber));
                OnPropertyChanged(nameof(ShowPinPad));
                OnPropertyChanged(nameof(CanSignIn));
            }
        }
    }

    public string StaffNumber
    {
        get => _staffNumber;
        set
        {
            if (SetProperty(ref _staffNumber, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Pin
    {
        get => _pin;
        private set
        {
            if (SetProperty(ref _pin, value))
            {
                OnPropertyChanged(nameof(PinMasked));
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Shown only in demo mode: the seeded staff (PIN / card).</summary>
    public string? DemoHint => _ctx.Options.Mock
        ? $"DEMO staff (staff number / PIN): S-1001 cashier / {R007.Pos.Core.Mock.MockData.CashierPin}, S-1002 waiter / {R007.Pos.Core.Mock.MockData.WaiterPin}, S-1003 supervisor / {R007.Pos.Core.Mock.MockData.SupervisorPin} (username cashier|waiter|supervisor with the same PIN as password). Card UIDs: {R007.Pos.Core.Mock.MockData.CashierNfc} (cashier), {R007.Pos.Core.Mock.MockData.SupervisorNfc} (supervisor). Set R007_Pos__RequireNfcAndPin=true to try the NFC + PIN station."
        : null;

    /// <summary>Sign in with username + password instead of staff number + PIN (not offered on NFC + PIN stations).</summary>
    public bool UsePassword
    {
        get => _usePassword;
        private set
        {
            if (SetProperty(ref _usePassword, value))
            {
                Error = null;
                OnPropertyChanged(nameof(ShowPinEntry));
                OnPropertyChanged(nameof(ShowPasswordEntry));
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Username
    {
        get => _username;
        set
        {
            if (SetProperty(ref _username, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Set from the password box; cleared as soon as it has been used.</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                OnPropertyChanged(nameof(CanSignIn));
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowPinEntry => ShowPinPad && !UsePassword;

    public bool ShowPasswordEntry => Step == LoginStep.Credentials && UsePassword;

    public bool CanTogglePassword => !RequiresNfcAndPin;

    public string ToggleText => UsePassword ? "Use staff number and PIN" : "Use username and password";

    public RelayCommand TogglePasswordCommand { get; }

    public string PinMasked => new('●', Pin.Length);

    public bool RequiresNfcAndPin => _ctx.Options.RequireNfcAndPin;

    public bool ShowStaffNumber => Step == LoginStep.Credentials;

    public bool ShowPinPad => Step != LoginStep.TapCard;

    public string Prompt => Step switch
    {
        LoginStep.TapCard => "Tap your staff card to start",
        LoginStep.EnterPin => "Card read. Enter your PIN",
        _ => "Sign in: staff number and PIN, or tap your card",
    };

    public bool CanSignIn => !IsBusy && Step switch
    {
        LoginStep.Credentials when UsePassword => !string.IsNullOrWhiteSpace(Username) && Password.Length > 0,
        LoginStep.Credentials => Pin.Length >= 4 && !string.IsNullOrWhiteSpace(StaffNumber),
        LoginStep.EnterPin => Pin.Length >= 4 && _nfcUid is not null,
        _ => false,
    };

    public RelayCommand KeyCommand { get; }

    public RelayCommand BackspaceCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand CancelCardCommand { get; }

    public AsyncRelayCommand SignInCommand { get; }

    private void PressKey(string? key)
    {
        if (!string.IsNullOrEmpty(key) && key.Length == 1 && char.IsDigit(key[0]) && Pin.Length < MaxPinLength)
        {
            Pin += key;
        }
    }

    private void Reset()
    {
        Pin = string.Empty;
        Password = string.Empty;
        _nfcUid = null;
        Error = null;
        Step = RequiresNfcAndPin ? LoginStep.TapCard : LoginStep.Credentials;
    }

    /// <summary>An NFC card was read (keyboard-wedge or a dedicated reader).</summary>
    public async Task HandleScanAsync(string uid)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        Error = null;
        _nfcUid = uid.Trim().ToUpperInvariant();
        if (RequiresNfcAndPin)
        {
            Pin = string.Empty;
            Step = LoginStep.EnterPin;
            return;
        }

        // Card alone is allowed at this station.
        var ok = await RunAsync(async () =>
        {
            var result = await _ctx.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.NfcCard, null, _nfcUid)).ConfigureAwait(true);
            Complete(result);
        }).ConfigureAwait(true);
        if (!ok)
        {
            _nfcUid = null;
        }
    }

    private async Task SignInAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            AuthResult result;
            if (Step == LoginStep.EnterPin)
            {
                result = await SignInWithCardAndPinAsync(_nfcUid!, Pin).ConfigureAwait(true);
            }
            else if (UsePassword)
            {
                result = await _ctx.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Password, Username.Trim(), Password)).ConfigureAwait(true);
            }
            else
            {
                result = await _ctx.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, StaffNumber.Trim(), Pin)).ConfigureAwait(true);
            }

            Complete(result);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException or InvalidOperationException)
        {
            Error = Describe(ex);
            Pin = string.Empty;
            Password = string.Empty;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanSignIn));
            SignInCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task<AuthResult> SignInWithCardAndPinAsync(string uid, string pin)
    {
        var auth = _ctx.Auth;
        var cardOnly = await _ctx.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.NfcCard, null, uid)).ConfigureAwait(true);
        auth.SignIn(cardOnly, _ctx.Time.GetUtcNow()); // provisional: only so it can be revoked below

        try
        {
            var number = cardOnly.Staff.StaffNumber
                ?? throw new InvalidOperationException("This card's owner has no staff number, so the PIN cannot be checked.");
            var full = await _ctx.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, number, pin)).ConfigureAwait(true);
            if (full.Staff.Id != cardOnly.Staff.Id)
            {
                throw new InvalidOperationException("Card and PIN belong to different people.");
            }

            await _ctx.Api.LogoutAsync().ConfigureAwait(true); // revoke the card-only session
            return full;
        }
        catch
        {
            try
            {
                await _ctx.Api.LogoutAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
            {
                // best effort
            }

            auth.SignOut();
            throw;
        }
    }

    private void Complete(AuthResult result)
    {
        _ctx.Auth.SignIn(result, _ctx.Time.GetUtcNow());
        Pin = string.Empty;
        Password = string.Empty;
        StaffNumber = string.Empty;
        Username = string.Empty;
        _nfcUid = null;
        Step = RequiresNfcAndPin ? LoginStep.TapCard : LoginStep.Credentials;
        SignedIn?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A screen or dialog that consumes barcode/NFC scans (keyboard-wedge or dedicated reader).</summary>
public interface IScanTarget
{
    Task HandleScanAsync(string data);
}
