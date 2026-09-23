using System.Globalization;

namespace R007.Pos.Core.Money;

/// <summary>
/// Money handling for the POS. Amounts are <see cref="decimal"/> everywhere (never floating point) and travel
/// on the wire as decimal strings. The POS never computes prices, tax or discounts: it only parses what the
/// API sends, formats it for display, and validates amounts staff type in (tendered cash, declared cash).
/// </summary>
public static class MoneyFormat
{
    /// <summary>Wire format used for amounts we send, e.g. <c>1500.0000</c> (matches the API convention).</summary>
    public const string WireFormat = "0.0000";

    /// <summary>Maximum fractional digits staff may type: NGN has 2 (kobo).</summary>
    public const int MaxEnteredDecimals = 2;

    private const NumberStyles ParseStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <summary>Parses an API decimal string using the invariant culture. Throws on malformed input.</summary>
    public static decimal ParseWire(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return decimal.Parse(value, ParseStyles, CultureInfo.InvariantCulture);
    }

    public static bool TryParseWire(string? value, out decimal amount) =>
        decimal.TryParse(value, ParseStyles, CultureInfo.InvariantCulture, out amount);

    /// <summary>Formats an amount for the wire (invariant culture, fixed 4 decimals).</summary>
    public static string ToWire(decimal amount) => amount.ToString(WireFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an amount typed by staff. Tolerates thousands separators, spaces and a leading currency symbol;
    /// rejects negatives, zero and more than two decimals (no silent rounding of money).
    /// </summary>
    public static bool TryParseEntered(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = text.Trim().TrimStart('₦', 'N', 'n').Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed <= 0m || decimal.Round(parsed, MaxEnteredDecimals) != parsed)
        {
            return false;
        }

        amount = parsed;
        return true;
    }

    /// <summary>Like <see cref="TryParseEntered"/> but permits zero (e.g. counted cash of nothing, opening float 0).</summary>
    public static bool TryParseEnteredAllowZero(string? text, out decimal amount)
    {
        if (!string.IsNullOrWhiteSpace(text) && text.Trim().Trim('0', '.', ',', ' ').Length == 0)
        {
            amount = 0m;
            return true;
        }

        return TryParseEntered(text, out amount);
    }

    /// <summary>Display format, e.g. <c>NGN 1,500.00</c> (invariant grouping; the receipt is API-rendered anyway).</summary>
    public static string Display(decimal amount, string currency = "NGN")
    {
        var symbol = string.Equals(currency, "NGN", StringComparison.OrdinalIgnoreCase) ? "₦" : currency + " ";
        var sign = amount < 0 ? "-" : string.Empty;
        return sign + symbol + Math.Abs(amount).ToString("#,##0.00", CultureInfo.InvariantCulture);
    }
}
