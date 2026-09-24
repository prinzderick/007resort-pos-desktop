namespace R007.Pos.Devices.Printing;

/// <summary>A byte sink for a printer: the Windows spooler (RAW), a file, memory. Keeps ESC/POS rendering independent of transport.</summary>
public interface IRawPrinterPort
{
    Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);
}

/// <summary>Real printer: renders ESC/POS and sends it to an <see cref="IRawPrinterPort"/>.</summary>
public sealed class EscPosReceiptPrinter(IRawPrinterPort port, int charactersPerLine = IReceiptPrinter.DefaultCharactersPerLine) : IReceiptPrinter
{
    public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) => port.GetStatusAsync(cancellationToken);

    public async Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var status = await port.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status != PrinterStatus.Ready)
        {
            throw new InvalidOperationException($"Printer is not ready: {status}.");
        }

        await port.WriteAsync(EscPosRenderer.Render(document, charactersPerLine), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Captures written bytes (tests, diagnostics).</summary>
public sealed class MemoryPrinterPort : IRawPrinterPort
{
    private readonly List<byte[]> _writes = [];

    public PrinterStatus Status { get; set; } = PrinterStatus.Ready;

    public IReadOnlyList<byte[]> Writes => _writes;

    public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

    public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        _writes.Add(data);
        return Task.CompletedTask;
    }
}
