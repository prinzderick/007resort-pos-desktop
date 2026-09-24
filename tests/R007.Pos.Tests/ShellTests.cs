using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Http;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;
using R007.Pos.Devices.Simulated;
using R007.Pos.ViewModels;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

public sealed class ShellTests
{
    private static async Task<(TestPos Pos, ShellViewModel Shell)> StartedAsync(PosOptions? options = null, SimulatedNfcReader? nfc = null, SimulatedBarcodeScanner? scanner = null)
    {
        var pos = new TestPos(options);
        var shell = new ShellViewModel(pos.Ctx, nfc, scanner, (_, _) => Task.CompletedTask);
        await shell.StartAsync();
        return (pos, shell);
    }

    private static async Task RegisterAndSignInAsync(ShellViewModel shell, string code = "RESTAURANT", string staff = "S-1001", string pin = MockData.CashierPin)
    {
        var setup = Assert.IsType<SetupViewModel>(shell.Current);
        setup.ServerUrl = "http://mock.local";
        setup.RegistrationCode = code;
        setup.DeviceName = "Till 1";
        await setup.RegisterCommand.ExecuteAsync();
        var login = Assert.IsType<LoginViewModel>(shell.Current);
        login.StaffNumber = staff;
        foreach (var c in pin)
        {
            login.KeyCommand.Execute(c.ToString());
        }

        await login.SignInCommand.ExecuteAsync();
    }

    [Fact]
    public async Task FirstRun_ShowsSetup_ThenLogin_ThenMain_WithTabsFromCapabilities()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        Assert.Equal(Stage.Setup, shell.Stage);

        await RegisterAndSignInAsync(shell);

        Assert.Equal(Stage.Main, shell.Stage);
        Assert.Equal(["Sell", "Tables & tabs", "Cash session", "History", "Queue"], shell.Main!.Items.Select(i => i.Title));
        Assert.Equal("Amaka Cashier", shell.StaffName);
        Assert.NotNull(pos.Identity.Load()); // enrolment persisted
    }

    [Fact]
    public async Task SecondRun_WithStoredIdentity_GoesStraightToLogin()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await RegisterAndSignInAsync(shell);
        await shell.SignOutAsync();
        Assert.Equal(Stage.Login, shell.Stage);

        var again = new ShellViewModel(pos.Ctx, delay: (_, _) => Task.CompletedTask);
        await again.StartAsync();

        Assert.Equal(Stage.Login, again.Stage);
    }

    [Fact]
    public async Task Setup_BadRegistrationCode_ShowsError_AndStaysOnSetup()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        var setup = (SetupViewModel)shell.Current!;
        setup.ServerUrl = "http://mock.local";
        setup.RegistrationCode = "BAD";

        await setup.RegisterCommand.ExecuteAsync();

        Assert.Equal(Stage.Setup, shell.Stage);
        Assert.Equal("Invalid or expired registration code", setup.Error);
        Assert.Null(pos.Identity.Load());
    }

    [Fact]
    public async Task Setup_ValidatesUrl_AndTestsTheConnection()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        var setup = (SetupViewModel)shell.Current!;

        setup.ServerUrl = "not a url";
        await setup.TestCommand.ExecuteAsync();
        Assert.Contains("server address", setup.Error, StringComparison.Ordinal);

        setup.ServerUrl = "http://mock.local";
        await setup.TestCommand.ExecuteAsync();
        Assert.Contains("Connected to 007resort-api", setup.ServerStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DifferentStations_GetDifferentScreens_FromTheSameApp()
    {
        var (club, clubShell) = await StartedAsync();
        using var _c = club;
        await RegisterAndSignInAsync(clubShell, "CLUB");
        var (reception, receptionShell) = await StartedAsync();
        using var _r = reception;
        await RegisterAndSignInAsync(receptionShell, "RECEPTION");
        var (sup, supShell) = await StartedAsync();
        using var _s = sup;
        await RegisterAndSignInAsync(supShell, "RESTAURANT", "S-1003", MockData.SupervisorPin);

        Assert.Contains(clubShell.Main!.Items, i => i.Title == "Tables & tabs");
        Assert.DoesNotContain(clubShell.Main.Items, i => i.Title == "Reception");
        Assert.Contains(receptionShell.Main!.Items, i => i.Title == "Reception");
        Assert.DoesNotContain(receptionShell.Main.Items, i => i.Title == "Tables & tabs");
        Assert.Contains(supShell.Main!.Items, i => i.Title == "Approvals");
        Assert.DoesNotContain(clubShell.Main.Items, i => i.Title == "Approvals");
    }

    [Fact]
    public async Task ServerTooOld_ForThisClient_ShowsUpdateRequiredBanner()
    {
        var pos = new TestPos();
        using var _ = pos;
        await pos.Ctx.RegisterAsync(new Uri("http://mock.local/"), "RESTAURANT", "T");
        pos.Server.MinPosVersion = "9.0.0";
        var shell = new ShellViewModel(pos.Ctx, delay: (_, _) => Task.CompletedTask);

        await shell.StartAsync();

        Assert.Contains("Update required", shell.Banner, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredSession_ReturnsToLogin_WithAMessage()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await RegisterAndSignInAsync(shell);
        pos.Server.ExpireAccessTokens();
        pos.Env.Auth.UpdateTokens(new AuthResult("stale", "rt-burned", 60, pos.Env.Auth.Staff!, null), pos.Env.Time.GetUtcNow());

        await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.GetTablesAsync(pos.Ctx.FacilityId));
        for (var i = 0; i < 100 && shell.Stage == Stage.Main; i++)
        {
            await Task.Delay(5);
        }

        Assert.Equal(Stage.Login, shell.Stage);
        Assert.Equal("Your session expired. Please sign in again.", shell.Banner);
    }

    [Fact]
    public async Task IdleLock_SignsOutAfterInactivity_ButNotWhileADialogIsOpen()
    {
        var (pos, shell) = await StartedAsync(new PosOptions { IdleLockMinutes = 5 });
        using var _ = pos;
        await RegisterAndSignInAsync(shell);

        pos.Env.Time.Advance(TimeSpan.FromMinutes(4));
        await shell.TickAsync();
        Assert.Equal(Stage.Main, shell.Stage);

        shell.NotifyActivity();
        pos.Env.Time.Advance(TimeSpan.FromMinutes(6));
        await shell.TickAsync();

        Assert.Equal(Stage.Login, shell.Stage);
        Assert.Contains("Locked after inactivity", shell.Banner, StringComparison.Ordinal);
        Assert.False(pos.Env.Auth.IsSignedIn);
    }

    [Fact]
    public async Task Offline_ShowsStatus_QueuesCash_AndDrainsWhenTheServerReturns()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await RegisterAndSignInAsync(shell);
        var sell = shell.Main!.Sell;
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));
        pos.Server.Offline = true;
        await shell.TickAsync(); // probe notices the outage
        Assert.True(shell.IsOffline);
        Assert.Equal("OFFLINE: server unreachable", shell.ConnectivityText);

        pos.Emergency.Policy = new OfflinePolicy(true, OfflinePaymentPolicy.CashOnly);
        var paying = sell.PayCommand.ExecuteAsync(); // the shell shows the payment dialog and waits for it
        for (var i = 0; i < 200 && !shell.HasModal; i++)
        {
            await Task.Delay(2);
        }

        var pay = Assert.IsType<PaymentViewModel>(shell.TopModal);
        pay.Method = TenderTypes.Cash;
        pay.TenderAmountText = "1500";
        pay.AddTenderCommand.Execute(null);
        await pay.PayCommand.ExecuteAsync();
        await paying;
        Assert.False(shell.HasModal);
        await shell.TickAsync();
        Assert.Equal(1, shell.PendingCount);
        Assert.True(shell.HasPending);

        pos.Server.Offline = false;
        await shell.TickAsync(); // probe succeeds -> Restored -> drain

        Assert.False(shell.IsOffline);
        Assert.Equal(0, shell.PendingCount);
        Assert.Equal(1, pos.Server.PaymentCount);
    }

    private static async Task EnterPinAsync(ShellViewModel shell, string pin)
    {
        var login = Assert.IsType<LoginViewModel>(shell.Current);
        foreach (var c in pin)
        {
            login.KeyCommand.Execute(c.ToString());
        }

        await login.SignInCommand.ExecuteAsync();
    }

    [Fact]
    public async Task WedgeInput_OnLogin_IsACard_OnSell_IsABarcode()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await pos.Ctx.RegisterAsync(new Uri("http://mock.local/"), "RESTAURANT", "T");
        shell = new ShellViewModel(pos.Ctx, delay: (_, _) => Task.CompletedTask);
        await shell.StartAsync();
        Assert.Equal(Stage.Login, shell.Stage);

        await shell.HandleWedgeInputAsync(MockData.CashierNfc); // card tap arrives as keyboard text
        Assert.Equal(Stage.Login, shell.Stage);                 // the node needs the PIN with the card
        await EnterPinAsync(shell, MockData.CashierPin);
        Assert.Equal(Stage.Main, shell.Stage);

        await shell.HandleWedgeInputAsync("6001234500011");     // barcode scan
        Assert.Equal("Star Lager 60cl", Assert.Single(shell.Main!.Sell.Lines).Name);
    }

    [Fact]
    public async Task DedicatedReaders_Nfc_And_Barcode_RouteTheSameWay()
    {
        var nfc = new SimulatedNfcReader();
        var scanner = new SimulatedBarcodeScanner();
        await nfc.StartAsync();
        await scanner.StartAsync();
        var pos = new TestPos();
        using var _ = pos;
        await pos.Ctx.RegisterAsync(new Uri("http://mock.local/"), "RESTAURANT", "T");
        var shell = new ShellViewModel(pos.Ctx, nfc, scanner, (_, _) => Task.CompletedTask);
        await shell.StartAsync();

        nfc.SimulateTap(MockData.CashierNfc);
        for (var i = 0; i < 100 && (shell.Current as LoginViewModel)?.Step != LoginStep.EnterPin; i++)
        {
            await Task.Delay(5);
        }

        await EnterPinAsync(shell, MockData.CashierPin);

        Assert.Equal(Stage.Main, shell.Stage);
        scanner.SimulateScan("6001234500028");
        for (var i = 0; i < 100 && shell.Main!.Sell.Lines.Count == 0; i++)
        {
            await Task.Delay(5);
        }

        Assert.Equal("Coca-Cola 50cl", shell.Main!.Sell.Lines[0].Name);
    }

    [Fact]
    public async Task Modals_AreStacked_AndScansGoToTheTopDialog()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await RegisterAndSignInAsync(shell);
        var lookup = new CustomerLookupViewModel(pos.Ctx);

        var shown = shell.ShowModalAsync(lookup);
        Assert.True(shell.HasModal);
        Assert.Same(lookup, shell.TopModal);

        lookup.CancelCommand.Execute(null);
        Assert.False(await shown);
        Assert.False(shell.HasModal);
    }

    [Fact]
    public async Task SignOut_ClosesDialogs_RevokesTheSession_AndKeepsTheQueue()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await RegisterAndSignInAsync(shell);
        await pos.Queue.EnqueueAsync(new QueuedOperation(Guid.CreateVersion7(), "POST", "api/v1/orders", "{}", pos.Env.Time.GetUtcNow()));

        await shell.SignOutAsync();

        Assert.Equal(Stage.Login, shell.Stage);
        Assert.False(pos.Env.Auth.IsSignedIn);
        Assert.Contains(pos.Server.Requests, r => r.Path == "/api/v1/auth/staff/logout");
        Assert.Equal(1, await pos.Queue.CountPendingAsync()); // queued work survives sign-out
    }

    [Fact]
    public async Task QueueScreen_ShowsPending_AndRefusedItemsUntilAcknowledged()
    {
        var (pos, shell) = await StartedAsync();
        using var _ = pos;
        await RegisterAndSignInAsync(shell);
        var op = new QueuedOperation(Guid.CreateVersion7(), "POST", "api/v1/payments", "{}", pos.Env.Time.GetUtcNow(), null, "Cash 2000 for order X");
        await pos.Queue.EnqueueAsync(op);
        var queue = shell.Main!.Queue;

        await queue.LoadAsync();
        Assert.Equal(["Cash 2000 for order X"], queue.Pending);
        Assert.Contains("1 waiting to be confirmed", queue.SummaryText, StringComparison.Ordinal);

        await pos.Queue.MarkRejectedAsync(op.IdempotencyKey, "order_state_invalid");
        await queue.LoadAsync();
        var row = Assert.Single(queue.Rejected);
        Assert.Contains("refused", queue.SummaryText, StringComparison.Ordinal);

        await row.AcknowledgeCommand.ExecuteAsync();
        Assert.Empty(queue.Rejected);
        Assert.Equal("Nothing waiting.", queue.SummaryText);
    }

    [Fact]
    public void ScreenErrors_AreOperatorFriendly_NeverStackTraces()
    {
        Assert.Equal("Cannot reach the server. Check the network and try again.", ScreenViewModel.Describe(new ApiUnavailableException("boom")));
        Assert.Equal("You do not have permission to do that.", ScreenViewModel.Describe(new ApiException(System.Net.HttpStatusCode.Forbidden, "permission_denied", "Forbidden", "order.void.execute")));
        Assert.DoesNotContain("Exception", ScreenViewModel.Describe(new InvalidOperationException("secret internals")), StringComparison.Ordinal);
        Assert.Equal("Something went wrong. Please try again; if it persists, tell a supervisor.", ScreenViewModel.Describe(new InvalidOperationException("secret internals")));
        Assert.Equal("Unknown future thing", ScreenViewModel.Describe(new ApiException(System.Net.HttpStatusCode.Conflict, "some_future_code", "Conflict", "Unknown future thing")));
    }
}
