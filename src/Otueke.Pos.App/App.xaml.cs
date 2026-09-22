using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Otueke.Pos.Core.Api;
using Otueke.Pos.Core.Configuration;
using Otueke.Pos.Devices.CashDrawer;
using Otueke.Pos.Devices.CustomerDisplay;
using Otueke.Pos.Devices.Nfc;
using Otueke.Pos.Devices.Printing;
using Otueke.Pos.Devices.Scanning;
using Otueke.Pos.Devices.Simulated;

namespace Otueke.Pos.App;

/// <summary>Composition root for the POS desktop application.</summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables(prefix: "OTUEKE_");

        builder.Services.Configure<PosOptions>(builder.Configuration.GetSection(PosOptions.SectionName));

        builder.Services.AddHttpClient<IOtuekeApiClient, OtuekeApiClient>((services, client) =>
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
