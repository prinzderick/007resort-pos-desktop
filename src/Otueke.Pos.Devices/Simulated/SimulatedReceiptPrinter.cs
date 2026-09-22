using Otueke.Pos.Devices.Printing;

namespace Otueke.Pos.Devices.Simulated;

/// <summary>In-memory printer for development and tests; records every printed document.</summary>
public sealed class SimulatedReceiptPrinter : IReceiptPrinter
{
    private readonly List<ReceiptDocument> _printed = [];

    public PrinterStatus Status { get; set; } = PrinterStatus.Ready;

    public IReadOnlyList<ReceiptDocument> PrintedDocuments => _printed;

    public Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public Task PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (Status != PrinterStatus.Ready)
        {
            throw new InvalidOperationException($"Printer is not ready: {Status}.");
        }

        _printed.Add(document);
        return Task.CompletedTask;
    }
}
