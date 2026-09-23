namespace R007.Pos.Devices.Printing;

/// <summary>Writes a plain-text preview of each receipt to a <see cref="TextWriter"/> (development, demos).</summary>
public sealed class ConsoleReceiptPrinter(TextWriter output, int charactersPerLine = IReceiptPrinter.DefaultCharactersPerLine) : IReceiptPrinter
{
    public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(PrinterStatus.Ready);

    public Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        output.WriteLine(new string('=', charactersPerLine));
        output.Write(EscPosRenderer.RenderText(document, charactersPerLine));
        output.WriteLine(new string('=', charactersPerLine));
        output.Flush();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes each receipt to <c>receipt-yyyyMMdd-HHmmss-fff-n.txt</c> (preview) and <c>.bin</c> (exact ESC/POS bytes) in a
/// folder. Useful for demos on non-Windows machines, tests, and diagnosing a real printer (replay the .bin).
/// </summary>
public sealed class FileReceiptPrinter(string directory, int charactersPerLine = IReceiptPrinter.DefaultCharactersPerLine, TimeProvider? time = null) : IReceiptPrinter
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _counter;

    public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(PrinterStatus.Ready);

    public async Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Directory.CreateDirectory(directory);
        var n = Interlocked.Increment(ref _counter);
        var stem = Path.Combine(directory, $"receipt-{_time.GetUtcNow():yyyyMMdd-HHmmss-fff}-{n}");
        await File.WriteAllTextAsync(stem + ".txt", EscPosRenderer.RenderText(document, charactersPerLine), cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(stem + ".bin", EscPosRenderer.Render(document, charactersPerLine), cancellationToken).ConfigureAwait(false);
    }
}
