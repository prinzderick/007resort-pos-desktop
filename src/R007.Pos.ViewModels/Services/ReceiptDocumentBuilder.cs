using System.Globalization;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.Devices.Printing;

namespace R007.Pos.ViewModels.Services;

/// <summary>
/// Lays out the API's structured <see cref="Receipt"/> for an 80 mm printer (48 columns). Only formatting happens
/// here: every amount printed is exactly what the API returned (no totals, tax or change are computed).
/// </summary>
public static class ReceiptDocumentBuilder
{
    public static ReceiptDocument Build(Receipt r, int width = IReceiptPrinter.DefaultCharactersPerLine, bool reprint = false)
    {
        ArgumentNullException.ThrowIfNull(r);
        var lines = new List<ReceiptLine>();
        var rule = new string('-', width);

        void Center(string text, bool bold = false, bool big = false) => lines.Add(new ReceiptLine(text, ReceiptAlignment.Center, bold, big));
        void Left(string text, bool bold = false) => lines.Add(new ReceiptLine(text, ReceiptAlignment.Left, bold));
        void Pair(string left, string right, bool bold = false) => lines.Add(new ReceiptLine(TwoColumns(left, right, width), ReceiptAlignment.Left, bold));
        string Money(decimal amount) => MoneyFormat.Display(amount, r.Currency);

        reprint |= r.Duplicate == true; // the node marks a counted reprint DUPLICATE in the snapshot it returns
        if (reprint)
        {
            Center("*** DUPLICATE ***", bold: true);
        }

        Center(r.SiteName ?? r.BusinessName ?? "007 Resort & Spa", bold: true, big: true);
        if (!string.IsNullOrWhiteSpace(r.BusinessName) && !string.Equals(r.SiteName, r.BusinessName, StringComparison.Ordinal))
        {
            Center(r.BusinessName); // the legal/business name printed under the site title (may be long: normal size, wraps)
        }

        Center(r.FacilityName, bold: true);
        if (!string.IsNullOrWhiteSpace(r.SiteAddress))
        {
            Center(r.SiteAddress);
        }

        if (!string.IsNullOrWhiteSpace(r.VatNumber))
        {
            Center($"VAT No: {r.VatNumber}");
        }

        Left(rule);
        Pair($"Receipt {r.Number}", LocalTime(r.IssuedAt).ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(r.CashierName))
        {
            Left($"Cashier: {r.CashierName}");
        }

        if (!string.IsNullOrWhiteSpace(r.Terminal))
        {
            Left($"Terminal: {r.Terminal}");
        }

        if (!string.IsNullOrWhiteSpace(r.TableLabel))
        {
            Left($"Table: {r.TableLabel}");
        }

        if (r.OrderNumbers is { Count: > 0 })
        {
            Left($"Order: {string.Join(", ", r.OrderNumbers)}");
        }

        Left(rule);
        foreach (var item in r.Lines)
        {
            Left(item.Name);
            Pair($"  {item.Quantity} x {Money(item.UnitPrice)}", Money(item.LineTotal));
        }

        Left(rule);
        Pair("Subtotal", Money(r.Subtotal));
        if (r.DiscountTotal != 0m)
        {
            Pair("Discount", "-" + Money(r.DiscountTotal));
        }

        if (r.TaxTotal != 0m)
        {
            Pair("Tax", Money(r.TaxTotal));
        }

        Pair("TOTAL", Money(r.Total), bold: true);
        Left(rule);
        foreach (var tender in r.Tenders)
        {
            Pair(TenderLabel(tender), Money(tender.Amount));
            if (tender.TenderType == TenderTypes.Cash && tender.Tendered is { } handed && handed != tender.Amount)
            {
                Pair("  Tendered", Money(handed));
            }
        }

        if (r.ChangeGiven is { } change and > 0m)
        {
            Pair("Change", Money(change));
        }

        // A partial settlement: say what is still owed (the node's figure), so a part-paid receipt is never mistaken for a paid one.
        if (r.BalanceDue is { } balance and > 0m)
        {
            Pair("BALANCE DUE", Money(balance), bold: true);
        }

        Left(rule);
        if (!string.IsNullOrWhiteSpace(r.Footer))
        {
            Center(r.Footer);
        }

        var cash = !reprint && r.Tenders.Any(t => t.TenderType == TenderTypes.Cash);
        return new ReceiptDocument(lines, CutPaper: true, QrData: r.QrPayload, OpenDrawer: cash);
    }

    /// <summary>Left text and right text on one line; if they do not fit the left text is truncated (never the amount).</summary>
    public static string TwoColumns(string left, string right, int width)
    {
        var l = EscPosRenderer.Sanitize(left);
        var rr = EscPosRenderer.Sanitize(right);
        var space = width - rr.Length - 1;
        if (space < 1)
        {
            return rr;
        }

        if (l.Length > space)
        {
            l = l[..space];
        }

        return l + new string(' ', width - l.Length - rr.Length) + rr;
    }

    private static string TenderLabel(ReceiptTender t)
    {
        var name = t.TenderType switch
        {
            TenderTypes.Cash => "Cash",
            TenderTypes.Card => "Card",
            TenderTypes.PosTerminal => "POS terminal",
            TenderTypes.Transfer => "Transfer",
            _ => t.TenderType,
        };
        return string.IsNullOrWhiteSpace(t.Reference) ? name : $"{name} ({t.Reference})";
    }

    /// <summary>Nigeria is UTC+1 all year (no DST): receipts print local time.</summary>
    private static DateTimeOffset LocalTime(DateTimeOffset utc) => utc.ToOffset(TimeSpan.FromHours(1));
}
