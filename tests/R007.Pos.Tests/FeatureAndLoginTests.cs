using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Terminal;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

public sealed class FeatureAndLoginTests
{
    private static Staff StaffWith(params string[] permissions) => new(Guid.NewGuid(), "T", "S-1", [], permissions, null);

    [Fact]
    public void Features_AreAFunctionOfCapabilitiesAndPermissions_NotStationNames()
    {
        var supermarket = new FacilityCapabilities(Guid.NewGuid(), [Capabilities.Pos, Capabilities.PaymentAcceptance, Capabilities.BarcodeSales, Capabilities.ReceiptPrinting], null);
        var cashier = StaffWith(Permissions.OrderCreate, Permissions.PaymentTake, Permissions.ReceiptReprint);

        var f = TerminalFeatures.From(supermarket, cashier, false);

        Assert.True(f.CanSell);
        Assert.True(f.CanPay);
        Assert.True(f.CanScanBarcodes);
        Assert.True(f.CanReprint);
        Assert.False(f.ShowTables);      // no table service / tab capability
        Assert.False(f.CanOpenTabs);
        Assert.False(f.CanSend);         // no kitchen/bar routing
        Assert.False(f.CanBook);
        Assert.False(f.CanVoid);         // permission not held
        Assert.False(f.CanSplit);
    }

    [Fact]
    public void SameStaff_DifferentFacility_DifferentScreens()
    {
        var staff = StaffWith(Permissions.OrderCreate, Permissions.OrderSend, Permissions.TabView, Permissions.TabOpen, Permissions.BookingCreate, Permissions.PaymentTake);
        var club = new FacilityCapabilities(Guid.NewGuid(), [Capabilities.Pos, Capabilities.OpenTab, Capabilities.TableService, Capabilities.BarRouting], new OperatingRules(null, null, true, null, null, null, null, null, null));
        var reception = new FacilityCapabilities(Guid.NewGuid(), [Capabilities.Pos, Capabilities.Booking, Capabilities.Ticketing], null);

        var atClub = TerminalFeatures.From(club, staff, false);
        var atReception = TerminalFeatures.From(reception, staff, false);

        Assert.True(atClub.ShowTables && atClub.CanOpenTabs && atClub.CanSend && !atClub.CanBook);
        Assert.True(atReception.CanBook && !atReception.ShowTables && !atReception.CanSend);
    }

    [Fact]
    public void OperatingRule_CanDisableTabsEvenWhenCapabilityIsOn()
    {
        var caps = new FacilityCapabilities(Guid.NewGuid(), [Capabilities.Pos, Capabilities.OpenTab], new OperatingRules(null, null, false, null, null, null, null, null, null));

        Assert.False(TerminalFeatures.From(caps, StaffWith(Permissions.OrderCreate, Permissions.TabOpen), false).CanOpenTabs);
    }

    [Fact]
    public void NoStaffOrNoCapabilities_MeansNothingIsOffered()
    {
        var f = TerminalFeatures.None;

        Assert.False(f.AnyPosScreen);
        Assert.False(f.CanSell);
    }

    [Fact]
    public void ApprovalHolders_GetTheSupervisorInbox()
    {
        var caps = new FacilityCapabilities(Guid.NewGuid(), [Capabilities.Pos], null);

        Assert.True(TerminalFeatures.From(caps, StaffWith(Permissions.OrderVoidApprove), false).CanDecideApprovals);
        Assert.False(TerminalFeatures.From(caps, StaffWith(Permissions.OrderVoidExecute), false).CanDecideApprovals);
    }

    // Login -------------------------------------------------------------------------------------------------------
    private static async Task<(TestPos Pos, LoginViewModel Login)> LoginEnvAsync(bool requireNfcAndPin = false)
    {
        var pos = new TestPos(new PosOptions { RequireNfcAndPin = requireNfcAndPin });
        await pos.Ctx.RegisterAsync(new Uri("http://mock.local/"), "RESTAURANT", "T");
        return (pos, new LoginViewModel(pos.Ctx));
    }

    private static void Type(LoginViewModel login, string pin)
    {
        foreach (var c in pin)
        {
            login.KeyCommand.Execute(c.ToString());
        }
    }

    [Fact]
    public async Task Login_WithStaffNumberAndPin_SignsIn()
    {
        var (pos, login) = await LoginEnvAsync();
        using var _ = pos;
        var signedIn = false;
        login.SignedIn += (_, _) => signedIn = true;

        login.StaffNumber = "S-1001";
        Type(login, MockData.CashierPin);
        Assert.Equal("●●●●", login.PinMasked);
        await login.SignInCommand.ExecuteAsync();

        Assert.True(signedIn);
        Assert.Equal("Amaka Cashier", pos.Ctx.Staff!.DisplayName);
        Assert.Equal(string.Empty, login.Pin); // credential is never kept
    }

    [Fact]
    public async Task Login_WrongPin_ShowsFriendlyError_AndClearsPin()
    {
        var (pos, login) = await LoginEnvAsync();
        using var _ = pos;
        login.StaffNumber = "S-1001";
        Type(login, "0000");

        await login.SignInCommand.ExecuteAsync();

        Assert.Equal("Those credentials were not recognised.", login.Error);
        Assert.Equal(string.Empty, login.Pin);
        Assert.False(pos.Env.Auth.IsSignedIn);
    }

    [Fact]
    public async Task Login_PinPad_IgnoresNonDigits_AndCapsLength()
    {
        var (pos, login) = await LoginEnvAsync();
        using var _ = pos;

        login.KeyCommand.Execute("a");
        login.KeyCommand.Execute("12");
        for (var i = 0; i < 20; i++)
        {
            login.KeyCommand.Execute("7");
        }

        Assert.Equal(LoginViewModel.MaxPinLength, login.Pin.Length);
        login.BackspaceCommand.Execute(null);
        Assert.Equal(LoginViewModel.MaxPinLength - 1, login.Pin.Length);
    }

    [Fact]
    public async Task Login_NfcCardAlone_NeverSignsIn_ItAsksForThePin_OnAnyStation()
    {
        var (pos, login) = await LoginEnvAsync();
        using var _ = pos;

        await login.HandleScanAsync(MockData.CashierNfc.ToLowerInvariant());

        Assert.False(pos.Env.Auth.IsSignedIn);
        Assert.Equal(LoginStep.EnterPin, login.Step);
        Assert.DoesNotContain(pos.Server.Requests, r => r.Path == "/api/v1/auth/staff/login"); // the card is not sent until the PIN is entered
    }

    [Fact]
    public async Task Login_UnknownCard_IsRejected_OncePinIsEntered()
    {
        var (pos, login) = await LoginEnvAsync();
        using var _ = pos;

        await login.HandleScanAsync("DEADBEEF");
        Type(login, MockData.CashierPin);
        await login.SignInCommand.ExecuteAsync();

        Assert.False(pos.Env.Auth.IsSignedIn);
        Assert.NotNull(login.Error);
    }

    [Fact]
    public async Task Login_NfcPinStation_StartsAtTapCard()
    {
        var (pos, login) = await LoginEnvAsync(requireNfcAndPin: true);
        using var _ = pos;
        Assert.Equal(LoginStep.TapCard, login.Step);
        Assert.False(login.ShowPinPad);

        await login.HandleScanAsync(MockData.CashierNfc);

        Assert.False(pos.Env.Auth.IsSignedIn);
        Assert.Equal(LoginStep.EnterPin, login.Step);
    }

    [Fact]
    public async Task Login_NfcPlusPin_IsOneCall_CardUidIsTheIdentifier_PinIsTheSecret()
    {
        var (pos, login) = await LoginEnvAsync(requireNfcAndPin: true);
        using var _ = pos;
        await login.HandleScanAsync(MockData.CashierNfc);
        Type(login, MockData.CashierPin);

        await login.SignInCommand.ExecuteAsync();

        Assert.True(pos.Env.Auth.IsSignedIn);
        var call = Assert.Single(pos.Server.Requests, r => r.Path == "/api/v1/auth/staff/login");
        Assert.Contains("\"credentialType\":\"NFC_CARD\"", call.Body, StringComparison.Ordinal);
        Assert.Contains($"\"identifier\":\"{MockData.CashierNfc.ToUpperInvariant()}\"", call.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(pos.Server.Requests, r => r.Path.EndsWith("/logout", StringComparison.Ordinal)); // no provisional session to revoke
    }

    [Fact]
    public async Task Login_NfcPlusWrongPin_FailsAndLeavesNoSession()
    {
        var (pos, login) = await LoginEnvAsync(requireNfcAndPin: true);
        using var _ = pos;
        await login.HandleScanAsync(MockData.CashierNfc);
        Type(login, "9999"); // valid PIN, but the supervisor's, not the card owner's

        await login.SignInCommand.ExecuteAsync();

        Assert.False(pos.Env.Auth.IsSignedIn);
        Assert.NotNull(login.Error);
        Assert.Equal(LoginStep.EnterPin, login.Step); // can retry the PIN without re-tapping
    }

    [Fact]
    public async Task Login_CardAndPinFromDifferentPeople_IsRefused()
    {
        var (pos, login) = await LoginEnvAsync(requireNfcAndPin: true);
        using var _ = pos;
        await login.HandleScanAsync(MockData.SupervisorNfc);
        Type(login, MockData.CashierPin);

        await login.SignInCommand.ExecuteAsync();

        Assert.False(pos.Env.Auth.IsSignedIn);
    }

    [Fact]
    public async Task Login_UsernameAndPassword_IsAnAlternative_ButNotOnNfcPinStations()
    {
        var (pos, login) = await LoginEnvAsync();
        using var _ = pos;
        Assert.True(login.CanTogglePassword);
        login.TogglePasswordCommand.Execute(null);
        Assert.True(login.ShowPasswordEntry);
        Assert.False(login.ShowPinEntry);

        login.Username = "supervisor";
        login.Password = MockData.SupervisorPin;
        Assert.True(login.CanSignIn);
        await login.SignInCommand.ExecuteAsync();

        Assert.Equal("Ngozi Supervisor", pos.Ctx.Staff!.DisplayName);
        Assert.Equal(string.Empty, login.Password);
        var call = pos.Server.Requests.Single(r => r.Path == "/api/v1/auth/staff/login");
        Assert.Contains("\"credentialType\":\"PASSWORD\"", call.Body, StringComparison.Ordinal);

        var (strong, strongLogin) = await LoginEnvAsync(requireNfcAndPin: true);
        using var _2 = strong;
        Assert.False(strongLogin.CanTogglePassword);
        Assert.False(strongLogin.TogglePasswordCommand.CanExecute(null));
    }
}
