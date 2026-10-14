using System.Text.Json;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Security;
using R007.Pos.Core.Terminal;
using R007.Pos.ViewModels;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

/// <summary>The hidden Connection dialog: one terminal, one enrolment per server (property server and online server).</summary>
public sealed class ServerSwitchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r007-switch-" + Guid.NewGuid().ToString("N"));

    public ServerSwitchTests() => Directory.CreateDirectory(_dir);

    private string IdentityPath => Path.Combine(_dir, "identity.bin");

    private static DeviceIdentity Identity(string url) =>
        new(new Uri(url), Guid.NewGuid(), "token-for-" + url, "Till 1", Guid.NewGuid(), null, "Restaurant");

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
    public void FileStore_KeepsOneEnrolmentPerServer_AndSwitchesTheActiveOne()
    {
        var store = new FileDeviceIdentityStore(IdentityPath, new InsecureKeyProtector());
        var local = Identity("http://192.168.1.75");
        var online = Identity("https://api.example.test");

        store.Save(local);
        store.Save(online);

        Assert.Equal(2, store.All().Count);
        Assert.Equal(online.DeviceId, store.Load()!.DeviceId); // the last enrolment is the active one

        store.Activate(local.ServerUrl);
        Assert.Equal(local.DeviceId, store.Load()!.DeviceId);

        store.Activate(new Uri("https://another.example.test"));
        Assert.Null(store.Load()); // no enrolment there yet ...
        Assert.Equal(2, store.All().Count); // ... and the others are untouched
    }

    [Fact]
    public void FileStore_Clear_ForgetsOnlyTheActiveServer()
    {
        var store = new FileDeviceIdentityStore(IdentityPath, new InsecureKeyProtector());
        var local = Identity("http://192.168.1.75");
        var online = Identity("https://api.example.test");
        store.Save(local);
        store.Save(online);

        store.Clear();

        Assert.Null(store.Load());
        var remaining = Assert.Single(store.All());
        Assert.Equal(local.DeviceId, remaining.DeviceId);

        store.Activate(local.ServerUrl);
        store.Clear();
        Assert.Empty(store.All());
        Assert.False(File.Exists(IdentityPath)); // nothing left: the file goes too
    }

    [Fact]
    public void FileStore_TreatsCaseAndTrailingSlashAsTheSameServer()
    {
        var store = new FileDeviceIdentityStore(IdentityPath, new InsecureKeyProtector());

        store.Save(Identity("HTTP://192.168.1.75/"));
        store.Save(Identity("http://192.168.1.75"));

        Assert.Single(store.All());
    }

    [Fact]
    public void FileStore_ReadsTheOlderSingleEnrolmentFile()
    {
        var protector = new InsecureKeyProtector();
        var older = Identity("http://192.168.1.212");
        SecureFile.WriteProtected(IdentityPath, JsonSerializer.SerializeToUtf8Bytes(older), protector); // the pre-switcher format

        var store = new FileDeviceIdentityStore(IdentityPath, protector);

        Assert.Equal(older.DeviceId, store.Load()!.DeviceId);
        Assert.Single(store.All());
    }

    [Fact]
    public async Task Shell_SwitchToAnotherServer_NeedsSetupThere_AndSwitchingBackRestoresTheEnrolment()
    {
        using var pos = new TestPos();
        var shell = new ShellViewModel(pos.Ctx, null, null, (_, _) => Task.CompletedTask);
        await shell.StartAsync();
        await EnrolAndSignInAsync(shell);
        Assert.Equal(Stage.Main, shell.Stage);

        await shell.SwitchServerAsync(new Uri("http://other.local"));

        Assert.Equal(Stage.Setup, shell.Stage); // the other server has not enrolled this terminal yet
        Assert.Equal("http://other.local", Assert.IsType<SetupViewModel>(shell.Current).ServerUrl);
        Assert.False(shell.IsSignedIn);
        Assert.Null(pos.Ctx.Identity);
        Assert.Single(pos.Identity.All()); // the first enrolment is kept

        await shell.SwitchServerAsync(new Uri("http://mock.local"));

        Assert.Equal(Stage.Login, shell.Stage);
        Assert.NotNull(pos.Ctx.Identity); // back on the first server: still enrolled, no new code needed
    }

    [Fact]
    public async Task Shell_SwitchIsRefused_WhileItemsAreWaitingToBeConfirmed()
    {
        using var pos = new TestPos();
        var shell = new ShellViewModel(pos.Ctx, null, null, (_, _) => Task.CompletedTask);
        await shell.StartAsync();
        await EnrolAndSignInAsync(shell);
        await pos.Queue.EnqueueAsync(new QueuedOperation(Guid.CreateVersion7(), "POST", "api/v1/payments", "{}", pos.Env.Time.GetUtcNow(), Guid.NewGuid(), "test"));

        await shell.SwitchServerAsync(new Uri("http://other.local"));

        Assert.Equal(Stage.Main, shell.Stage); // nothing was touched
        Assert.True(shell.IsSignedIn);
        Assert.Equal("http://mock.local/", pos.Ctx.Endpoint.Current.AbsoluteUri);
        Assert.Contains("waiting", shell.Banner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dialog_ListsTheServers_AndReturnsTheChosenOne()
    {
        using var pos = new TestPos(new PosOptions { OnlineUrl = new Uri("https://api.example.test"), LocalUrl = new Uri("http://192.168.1.75") });
        await pos.SignInAsync(); // enrolled on http://mock.local

        var dialog = new ConnectionViewModel(pos.Ctx);

        Assert.True(dialog.IsUnlocked);
        Assert.Single(dialog.Choices, c => c.IsCurrent);
        var online = Assert.Single(dialog.Choices, c => c.Label == "Online server");
        Assert.Equal("Needs a registration code", online.Status);

        dialog.ChooseCommand.Execute(online);

        Assert.Equal(new Uri("https://api.example.test"), dialog.Chosen);
        Assert.True(await dialog.Closed);
    }

    [Fact]
    public async Task Dialog_WithAPin_StaysLockedUntilTheRightPin()
    {
        using var pos = new TestPos(new PosOptions { ConnectionPin = "4242" });
        await pos.SignInAsync();
        var dialog = new ConnectionViewModel(pos.Ctx);
        Assert.True(dialog.IsLocked);

        dialog.EnteredPin = "0000";
        dialog.UnlockCommand.Execute(null);
        Assert.True(dialog.IsLocked);
        Assert.Equal("Wrong PIN.", dialog.Error);

        dialog.EnteredPin = "4242";
        dialog.UnlockCommand.Execute(null);
        Assert.True(dialog.IsUnlocked);
        Assert.Null(dialog.Error);
    }

    [Fact]
    public async Task Dialog_RejectsAnAddressThatIsNotAWebAddress()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var dialog = new ConnectionViewModel(pos.Ctx);

        dialog.NewUrl = "not an address";
        dialog.UseNewCommand.Execute(null);

        Assert.Null(dialog.Chosen);
        Assert.True(dialog.HasError);
    }

    private static async Task EnrolAndSignInAsync(ShellViewModel shell)
    {
        var setup = Assert.IsType<SetupViewModel>(shell.Current);
        setup.ServerUrl = "http://mock.local";
        setup.RegistrationCode = "RESTAURANT";
        setup.DeviceName = "Till 1";
        await setup.RegisterCommand.ExecuteAsync();
        var login = Assert.IsType<LoginViewModel>(shell.Current);
        login.StaffNumber = "S-1001";
        foreach (var c in MockData.CashierPin)
        {
            login.KeyCommand.Execute(c.ToString());
        }

        await login.SignInCommand.ExecuteAsync();
    }
}
