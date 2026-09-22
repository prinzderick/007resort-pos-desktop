namespace R007.Pos.Devices.Printing;

/// <summary>
/// 80 mm thermal receipt printer (ESC/POS command set). Implementations translate a
/// <see cref="ReceiptDocument"/> into device commands; callers never emit raw ESC/POS bytes.
/// Receipt content is produced by the API — the POS only renders it.
/// </summary>
public interface IReceiptPrinter
{
    /// <summary>Printable characters per line for Font A on 80 mm paper.</summary>
    const int DefaultCharactersPerLine = 48;

    Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default);
}

public enum PrinterStatus
{
    Ready,
    Offline,
    PaperOut,
    CoverOpen,
    Error,
}

public enum ReceiptAlignment
{
    Left,
    Center,
    Right,
}

public sealed record ReceiptLine(string Text, ReceiptAlignment Alignment = ReceiptAlignment.Left, bool Bold = false);

public sealed record ReceiptDocument(IReadOnlyList<ReceiptLine> Lines, bool CutPaper = true);
