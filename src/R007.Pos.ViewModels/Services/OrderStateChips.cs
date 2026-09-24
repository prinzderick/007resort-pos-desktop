using R007.Pos.Core.Api;

namespace R007.Pos.ViewModels.Services;

public enum ChipLevel
{
    Info,
    Warning,
    Success,
}

/// <summary>A small status label on a table, tab or order: "Bill printed", "Awaiting payment", "Collected - awaiting confirmation".</summary>
public sealed record OrderChip(string Text, ChipLevel Level)
{
    /// <summary>Name for XAML triggers (a string keeps the views free of enum namespaces).</summary>
    public string LevelName => Level.ToString();
}

/// <summary>Derives the chips from the API's additive bill fields only; nothing is computed here.</summary>
public static class OrderStateChips
{
    public const string BillPrinted = "Bill printed";
    public const string AwaitingPayment = "Awaiting payment";
    public const string CollectedAwaitingConfirmation = "Collected – awaiting confirmation";

    public static IReadOnlyList<OrderChip> For(bool billed, bool awaitingPayment, decimal pendingCollected)
    {
        var chips = new List<OrderChip>();
        if (billed)
        {
            chips.Add(new OrderChip(BillPrinted, ChipLevel.Info));
        }

        if (pendingCollected > 0m)
        {
            chips.Add(new OrderChip(CollectedAwaitingConfirmation, ChipLevel.Warning));
        }
        else if (awaitingPayment)
        {
            chips.Add(new OrderChip(AwaitingPayment, ChipLevel.Warning));
        }

        return chips;
    }

    public static IReadOnlyList<OrderChip> For(OrderSummary o) =>
        o.Status is OrderStatuses.Settled or OrderStatuses.Voided ? [] : For(o.IsBilled, o.AwaitingPayment == true, o.PendingCollected ?? 0m);

    public static IReadOnlyList<OrderChip> For(WorkingOrder o) =>
        o.IsClosed ? [] : For(o.IsBilled, o.AwaitingPayment == true, o.PendingCollected ?? 0m);

    /// <summary>A tab or table with several orders shows the union of their chips.</summary>
    public static IReadOnlyList<OrderChip> For(IEnumerable<OrderSummary> orders)
    {
        var list = orders.Where(o => o.Status is not (OrderStatuses.Settled or OrderStatuses.Voided)).ToList();
        return For(list.Any(o => o.IsBilled), list.Any(o => o.AwaitingPayment == true), list.Sum(o => o.PendingCollected ?? 0m));
    }

    public static string Joined(IReadOnlyList<OrderChip> chips) => string.Join(" | ", chips.Select(c => c.Text));
}
