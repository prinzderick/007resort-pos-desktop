using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Core.Http;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Security;
using R007.Pos.Core.Terminal;
using R007.Pos.Devices.CashDrawer;
using R007.Pos.Devices.CustomerDisplay;
using R007.Pos.Devices.Nfc;
using R007.Pos.Devices.Printing;
using R007.Pos.Devices.Scanning;
using R007.Pos.Devices.Simulated;
using R007.Pos.ViewModels;

namespace R007.Pos.App;

/// <summary>
/// Composition root. One application for every station: nothing here (or anywhere) is facility-specific. What a terminal
/// does is decided at run time by its device registration, the facility's capabilities and the signed-in staff
/// member's permissions, all from the API. <c>R007_MOCK=true</c> (or <c>Pos:Mock</c>) swaps the network transport for the
/// in-memory mock server so the whole app is demoable without a backend.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private CancellationTokenSource? _background;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            var builder = Host.CreateApplicationBuilder(e.Args);
            builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
            builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);
            builder.Configuration.AddEnvironmentVariables(prefix: "R007_");

            var options = builder.Configuration.GetSection(PosOptions.SectionName).Get<PosOptions>() ?? new PosOptions();
            // R007_MOCK=true maps to the root key "MOCK"; R007_Pos__Mock=true maps to Pos:Mock.
            options.Mock |= builder.Configuration.GetValue<bool>("MOCK");
            var dataDir = ResolveDataDirectory(options);
            Directory.CreateDirectory(dataDir);

            Register(builder.Services, options, dataDir);
            builder.Services.AddSingleton(sp => new MainWindow(sp.GetRequiredService<ShellViewModel>(), options.WedgeMaxKeyIntervalMs));

            _host = builder.Build();
            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            _background = new CancellationTokenSource();
            _ = StartShellAsync(shell);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            MessageBox.Show("The POS could not start:\n\n" + ex.Message + "\n\nSee the log in %LOCALAPPDATA%\\R007Pos\\logs.", "007 Resort & Spa POS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartShellAsync(ShellViewModel shell)
    {
        try
        {
            await shell.StartAsync().ConfigureAwait(true);
            await shell.RunBackgroundAsync(_background!.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CrashLog.Write(ex);
            shell.Banner = "Unexpected error: " + ex.Message;
        }
    }

    private static void Register(IServiceCollection services, PosOptions options, string dataDir)
    {
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);

        IKeyProtector protector = OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new InsecureKeyProtector();
        services.AddSingleton(protector);

        services.AddSingleton(new ServerEndpoint(options.Mock ? new Uri("http://mock.local/") : options.ApiBaseUrl));
        services.AddSingleton<AuthState>();
        services.AddSingleton<ConnectivityMonitor>();

        services.AddSingleton<HttpMessageHandler>(_ => options.Mock
            ? new MockApiHandler(TimeProvider.System, new MockOptions { Latency = TimeSpan.FromMilliseconds(120), VatRatePercent = 0m })
            : new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });

        services.AddSingleton(sp => PosHttp.CreateClient(
            sp.GetRequiredService<HttpMessageHandler>(),
            sp.GetRequiredService<AuthState>(),
            sp.GetRequiredService<ConnectivityMonitor>(),
            sp.GetRequiredService<ServerEndpoint>(),
            TimeSpan.FromSeconds(Math.Max(2, options.RequestTimeoutSeconds)),
            new RetryOptions(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IR007ApiClient>(sp => new R007ApiClient(sp.GetRequiredService<HttpClient>()));

        var secretsDir = Path.Combine(dataDir, options.Mock ? "mock" : "site");
        Directory.CreateDirectory(secretsDir);
        services.AddSingleton<IDeviceIdentityStore>(sp => new FileDeviceIdentityStore(Path.Combine(secretsDir, "device.bin"), sp.GetRequiredService<IKeyProtector>()));
        services.AddSingleton<IOfflineQueue>(sp => new EncryptedFileOfflineQueue(
            Path.Combine(secretsDir, "emergency-queue.bin"),
            sp.GetRequiredService<IKeyProtector>(),
            sp.GetRequiredService<TimeProvider>(),
            new OfflineQueueOptions { MaxEntries = options.Offline.MaxEntries, MaxAge = TimeSpan.FromMinutes(options.Offline.MaxAgeMinutes) }));
        services.AddSingleton(sp => new EmergencyQueue(sp.GetRequiredService<IOfflineQueue>(), sp.GetRequiredService<AuthState>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new QueueReplayService(sp.GetRequiredService<IOfflineQueue>(), sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<AuthState>()));

        // Devices ------------------------------------------------------------------------------------------------
        IRawPrinterPort? port = options.Printer.Kind.Equals("Windows", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(options.Printer.Name)
            ? new WindowsSpoolerPort(options.Printer.Name)
            : null;
        services.AddSingleton<IReceiptPrinter>(_ => options.Printer.Kind.ToUpperInvariant() switch
        {
            "WINDOWS" when port is not null => new EscPosReceiptPrinter(port, options.Printer.CharactersPerLine),
            "FILE" => new FileReceiptPrinter(options.Printer.OutputDirectory ?? Path.Combine(dataDir, "receipts"), options.Printer.CharactersPerLine),
            "CONSOLE" => new ConsoleReceiptPrinter(new DebugWriter(), options.Printer.CharactersPerLine),
            _ => new SimulatedReceiptPrinter(),
        });
        services.AddSingleton<ICashDrawer>(_ => port is not null ? new PrinterCashDrawer(port) : new SimulatedCashDrawer());
        services.AddSingleton<ICustomerDisplay, SimulatedCustomerDisplay>();
        // Card readers and scanners are keyboard-wedge devices handled by MainWindow; the dedicated-reader seams stay simulated.
        services.AddSingleton<INfcReader, SimulatedNfcReader>();
        services.AddSingleton<IBarcodeScanner, SimulatedBarcodeScanner>();

        services.AddSingleton(sp => new PosContext(
            sp.GetRequiredService<IR007ApiClient>(),
            sp.GetRequiredService<AuthState>(),
            sp.GetRequiredService<ConnectivityMonitor>(),
            sp.GetRequiredService<ServerEndpoint>(),
            sp.GetRequiredService<IOfflineQueue>(),
            sp.GetRequiredService<EmergencyQueue>(),
            sp.GetRequiredService<QueueReplayService>(),
            sp.GetRequiredService<IReceiptPrinter>(),
            sp.GetRequiredService<ICashDrawer>(),
            sp.GetRequiredService<IDeviceIdentityStore>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            HardwareId(dataDir),
            AppVersion()));
        services.AddSingleton(sp => new ShellViewModel(sp.GetRequiredService<PosContext>(), sp.GetRequiredService<INfcReader>(), sp.GetRequiredService<IBarcodeScanner>()));
    }

    private static string ResolveDataDirectory(PosOptions options) =>
        string.IsNullOrWhiteSpace(options.DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R007Pos")
            : options.DataDirectory;

    /// <summary>A stable, non-secret machine id sent at registration (a random GUID persisted next to the machine name).</summary>
    private static string HardwareId(string dataDir)
    {
        var path = Path.Combine(dataDir, "hardware-id.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Guid.NewGuid().ToString("N"));
        }

        var raw = Environment.MachineName + ":" + File.ReadAllText(path).Trim();
        return "pos-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
    }

    private static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write(e.Exception);
        e.Handled = true; // a till must not vanish mid-sale: log it, keep the app (and the emergency queue) alive
        if (MainWindow?.DataContext is ShellViewModel shell)
        {
            shell.Banner = "Something unexpected happened. If a sale was in progress, check the order before retrying.";
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _background?.Cancel();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private sealed class DebugWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) => System.Diagnostics.Debug.Write(value);

        public override void Write(string? value) => System.Diagnostics.Debug.Write(value);
    }
}

/// <summary>Tiny file log for crashes. Never logs request bodies, tokens or card data.</summary>
internal static class CrashLog
{
    public static void Write(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "R007Pos", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"pos-{DateTime.UtcNow:yyyyMMdd}.log"), $"{DateTime.UtcNow:O} {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch (IOException)
        {
            // logging must never take the till down
        }
    }
}
