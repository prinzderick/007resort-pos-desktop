using System.Globalization;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.Devices.Printing;

namespace R007.Pos.ViewModels.Services;

/// <summary>
/// Lays out the API's <see cref="PreBill"/> for an 80 mm printer. It is loudly NOT a receipt (top and bottom banners), shows the reprint
/// counter, and carries the pay-link QR when the node produced one. Only formatting happens here: every figure is the API's.
/// </summary>
public static class PreBillDocumentBuilder
{
    public const string TopBanner = "*** BILL - NOT A RECEIPT ***";
    public const string BottomBanner = "NOT A RECEIPT - NOT PROOF OF PAYMENT";

    public static ReceiptDocument Build(PreBill bill, int width = IReceiptPrinter.DefaultCharactersPerLine)
    {
        ArgumentNullException.ThrowIfNull(bill);
        var lines = new List<ReceiptLine>();
        var rule = new string('-', width);
        string Money(decimal amount) => MoneyFormat.Display(amount, bill.Currency);
        void Center(string text, bool bold = false, bool big = false) => lines.Add(new ReceiptLine(text, ReceiptAlignment.Center, bold, big));
        void Left(string text, bool bold = false) => lines.Add(new ReceiptLine(text, ReceiptAlignment.Left, bold));
        void Pair(string l, string r, bool bold = false) => lines.Add(new ReceiptLine(ReceiptDocumentBuilder.TwoColumns(l, r, width), ReceiptAlignment.Left, bold));

        Center(TopBanner, bold: true);
        if (bill.Reprint || bill.PrintCount > 1)
        {
            Center($"*** REPRINT #{bill.PrintCount} ***", bold: true);
        }

        if (!string.IsNullOrWhiteSpace(bill.Facility?.Name))
        {
            Center(bill.Facility!.Name!, bold: true, big: true);
        }

        Left(rule);
        Pair($"Order {bill.OrderNumber}", bill.PrintedAt.ToOffset(TimeSpan.FromHours(1)).ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture));
        Left($"Table: {(string.IsNullOrWhiteSpace(bill.TableLabel) ? "-" : bill.TableLabel)}");
        Left($"Waiter: {(string.IsNullOrWhiteSpace(bill.Waiter?.Name) ? "-" : bill.Waiter!.Name)}");
        Left(rule);
        foreach (var item in bill.Lines)
        {
            Left(item.Name);
            Pair($"  {item.Quantity} x {Money(item.UnitPrice)}", Money(item.LineTotal));
        }

        Left(rule);
        Pair("Subtotal", Money(bill.Subtotal));
        if (bill.DiscountTotal != 0m)
        {
            Pair("Discount", "-" + Money(bill.DiscountTotal));
        }

        foreach (var tax in bill.TaxLines ?? [])
        {
            Pair(tax.Label, Money(tax.Amount));
        }

        if ((bill.TaxLines is null || bill.TaxLines.Count == 0) && bill.TaxTotal != 0m)
        {
            Pair("Tax", Money(bill.TaxTotal));
        }

        Pair("TOTAL DUE", Money(bill.Total), bold: true);
        if (bill.AmountPaid > 0m)
        {
            Pair("Paid so far", Money(bill.AmountPaid));
            Pair("BALANCE DUE", Money(bill.BalanceDue), bold: true);
        }

        Left(rule);
        foreach (var wrapped in Wrap(string.IsNullOrWhiteSpace(bill.Disclaimer) ? "Pay only through the card machine, the transfer link or the cashier." : bill.Disclaimer!, width))
        {
            Center(wrapped);
        }
        string? qr = null;
        if (bill.PayLink is { Enabled: true } link)
        {
            if (!string.IsNullOrWhiteSpace(link.Reference))
            {
                Center($"Pay reference: {link.Reference}", bold: true);
            }

            qr = string.IsNullOrWhiteSpace(link.QrPayload) ? link.Url : link.QrPayload;
            if (qr is not null)
            {
                Center("Scan to pay online");
            }
        }

        Center(BottomBanner, bold: true);
        return new ReceiptDocument(lines, CutPaper: true, QrData: qr, OpenDrawer: false);
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = string.Empty;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = line.Length == 0 ? word : line + " " + word;
            }
        }

        if (line.Length > 0)
        {
            yield return line;
        }
    }
}
