using R007.Pos.Devices.Printing;

namespace R007.Pos.Devices.CashDrawer;

/// <summary>
/// Cash drawer wired to the receipt printer's kick connector: opening sends the ESC/POS pulse to the printer port.
/// (Cash receipts also kick automatically via <see cref="ReceiptDocument.OpenDrawer"/>.) The drawer has no sensor
/// through the printer, so <see cref="IsOpenAsync"/> reports what we last commanded, not a physical state.
/// </summary>
public sealed class PrinterCashDrawer(IRawPrinterPort port) : ICashDrawer
{
    public bool LastCommandedOpen { get; private set; }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await port.WriteAsync(EscPosRenderer.DrawerKick(), cancellationToken).ConfigureAwait(false);
        LastCommandedOpen = true;
    }

    public Task<bool> IsOpenAsync(CancellationToken cancellationToken = default) => Task.FromResult(LastCommandedOpen);
}
