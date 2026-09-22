using Otueke.Pos.Devices.Scanning;

namespace Otueke.Pos.Devices.Simulated;

/// <summary>Barcode scanner driven from code (dev tools / tests) via <see cref="SimulateScan"/>.</summary>
public sealed class SimulatedBarcodeScanner(TimeProvider timeProvider) : IBarcodeScanner
{
    private bool _started;

    public SimulatedBarcodeScanner()
        : this(TimeProvider.System)
    {
    }

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _started = false;
        return Task.CompletedTask;
    }

    public bool SimulateScan(string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);

        if (!_started)
        {
            return false;
        }

        BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(data, timeProvider.GetUtcNow()));
        return true;
    }
}
