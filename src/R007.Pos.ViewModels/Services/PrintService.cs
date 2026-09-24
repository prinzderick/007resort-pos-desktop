using R007.Pos.Core.Api;
using R007.Pos.Devices.Printing;

namespace R007.Pos.ViewModels.Services;

public sealed record PrintResult(bool Printed, string Message);

/// <summary>Fetches API-produced receipts and prints them. Printer trouble never fails a completed sale: it is reported.</summary>
public sealed class PrintService(IR007ApiClient api, IReceiptPrinter printer, int charactersPerLine = IReceiptPrinter.DefaultCharactersPerLine)
{
    public async Task<PrintResult> PrintReceiptAsync(Guid receiptId, bool reprint = false, string? qrOverride = null, CancellationToken ct = default)
    {
        Receipt receipt;
        try
        {
            receipt = await api.GetReceiptAsync(receiptId, reprint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return new PrintResult(false, "Could not fetch the receipt: " + (ex is ApiException a ? a.UserMessage : ex.Message));
        }

        if (qrOverride is not null)
        {
            receipt = receipt with { QrPayload = qrOverride };
        }

        return await PrintAsync(ReceiptDocumentBuilder.Build(receipt, charactersPerLine, reprint), ct).ConfigureAwait(false);
    }

    public async Task<PrintResult> PrintOrderReceiptAsync(Guid orderId, string? qrOverride = null, CancellationToken ct = default)
    {
        try
        {
            var receipt = await api.GetOrderReceiptAsync(orderId, ct).ConfigureAwait(false);
            if (qrOverride is not null)
            {
                receipt = receipt with { QrPayload = qrOverride };
            }

            return await PrintAsync(ReceiptDocumentBuilder.Build(receipt, charactersPerLine), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return new PrintResult(false, "Could not fetch the receipt: " + (ex is ApiException a ? a.UserMessage : ex.Message));
        }
    }

    /// <summary>Prints the pre-bill. Printer trouble is reported, never thrown: the bill is already recorded (and counted) on the node.</summary>
    public async Task<PrintResult> PrintBillAsync(PreBill bill, CancellationToken ct = default)
    {
        var result = await PrintAsync(PreBillDocumentBuilder.Build(bill, charactersPerLine), ct).ConfigureAwait(false);
        return result.Printed ? new PrintResult(true, "Bill printed.") : result with { Message = result.Message.Replace("Reprint from History once fixed.", "Print the bill again once the printer is fixed.", StringComparison.Ordinal) };
    }

    public async Task<PrintResult> PrintAsync(ReceiptDocument document, CancellationToken ct = default)
    {
        try
        {
            var status = await printer.GetStatusAsync(ct).ConfigureAwait(false);
            if (status != PrinterStatus.Ready)
            {
                return new PrintResult(false, $"Printer not ready ({status}). Reprint from History once fixed.");
            }

            await printer.PrintAsync(document, ct).ConfigureAwait(false);
            return new PrintResult(true, "Receipt printed.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return new PrintResult(false, "Printing failed: " + ex.Message);
        }
    }
}
