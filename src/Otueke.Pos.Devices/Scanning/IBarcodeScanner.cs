namespace Otueke.Pos.Devices.Scanning;

/// <summary>Barcode/QR scanner (products, tickets, booking references).</summary>
public interface IBarcodeScanner
{
    event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class BarcodeScannedEventArgs(string data, DateTimeOffset scannedAtUtc) : EventArgs
{
    public string Data { get; } = data;

    public DateTimeOffset ScannedAtUtc { get; } = scannedAtUtc;
}
