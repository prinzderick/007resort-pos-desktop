using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;
using R007.Pos.Devices.CashDrawer;
using R007.Pos.Devices.CustomerDisplay;
using R007.Pos.Devices.Nfc;
using R007.Pos.Devices.Printing;
using R007.Pos.Devices.Scanning;
using R007.Pos.Devices.Simulated;

namespace R007.Pos.App;

/// <summary>Composition root for the POS desktop application.</summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(prefix: "R007_");

        builder.Services.Configure<PosOptions>(builder.Configuration.GetSection(PosOptions.SectionName));

        builder.Services.AddHttpClient<IR007ApiClient, R007ApiClient>((services, client) =>
        {
            var options = services.GetRequiredService<IOptions<PosOptions>>().Value;
            client.BaseAddress = options.ApiBaseUrl;
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // TODO(phase-1): select real, vendor-specific drivers from configuration.
        builder.Services.AddSingleton<IReceiptPrinter, SimulatedReceiptPrinter>();
        builder.Services.AddSingleton<INfcReader, SimulatedNfcReader>();
        builder.Services.AddSingleton<IBarcodeScanner, SimulatedBarcodeScanner>();
        builder.Services.AddSingleton<ICashDrawer, SimulatedCashDrawer>();
        builder.Services.AddSingleton<ICustomerDisplay, SimulatedCustomerDisplay>();

        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
