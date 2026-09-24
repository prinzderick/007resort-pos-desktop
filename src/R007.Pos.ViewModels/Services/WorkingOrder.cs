using R007.Pos.Core.Api;

namespace R007.Pos.ViewModels.Services;

public sealed record WorkingLine(Guid Id, Guid ProductId, string Name, int Quantity, decimal UnitPrice, decimal? LineTotal, string? Notes, string Status, bool IsPending);

/// <summary>Where a new order is created (table, tab, channel). Facility comes from the registered device.</summary>
public sealed record OrderTarget(Guid FacilityId, Guid? TableId = null, Guid? TabId = null, string? Channel = null, string? CustomerName = null);

/// <summary>
/// What the cart shows. Normally a server-priced <see cref="Order"/>: every total is the API's. During an emergency
/// (API unreachable, facility allows offline orders) it holds only what we have queued: no server totals
/// (<see cref="Total"/> etc. are null), a display-only <see cref="EstimatedTotal"/>, and
/// <see cref="IsPendingConfirmation"/> so the UI can never present it as confirmed.
/// </summary>
public sealed record WorkingOrder(
    Guid Id,
    string Number,
    Guid FacilityId,
    Guid? TableId,
    Guid? TabId,
    string Status,
    int RowVersion,
    IReadOnlyList<WorkingLine> Lines,
    decimal? Subtotal,
    decimal? DiscountTotal,
    decimal? TaxTotal,
    decimal? Total,
    decimal? AmountPaid,
    decimal? BalanceDue,
    decimal EstimatedTotal,
    string Currency,
    bool IsPendingConfirmation,
    Guid? PendingApprovalId,
    string? BillState = null,
    int? BillPrintCount = null,
    int? BillReopenCount = null,
    bool? AwaitingPayment = null,
    decimal? PendingCollected = null,
    decimal? Collectable = null)
{
    public bool IsDraft => Status == OrderStatuses.Draft;

    public bool IsClosed => Status is OrderStatuses.Settled or OrderStatuses.Voided;

    public bool HasLines => Lines.Count > 0;

    /// <summary>The pre-bill was printed: the order is frozen (no lines, send, discount or void) until the bill is reopened.</summary>
    public bool IsBilled => BillState == BillStates.BillPrinted;

    /// <summary>A waiter already collected money on this bill that no cashier has confirmed yet.</summary>
    public bool HasPendingCollection => PendingCollected is > 0m;

    /// <summary>Nothing is left for the cashier to take: what remains is already collected by a waiter, awaiting confirmation.</summary>
    public bool FullyCollectedByWaiter => HasPendingCollection && Collectable is <= 0m;

    /// <summary>Default amount to collect: what the API says is still collectable (balance minus waiter-collected), else the balance, else (emergency only) the labelled estimate.</summary>
    public decimal AmountDue => Collectable ?? BalanceDue ?? EstimatedTotal;

    public static WorkingOrder FromServer(Order o) => new(
        o.Id,
        o.Number,
        o.FacilityId,
        o.TableId,
        o.TabId,
        o.Status,
        o.RowVersion,
        [.. o.Lines.Where(l => l.Status is not ("REMOVED" or "VOIDED")).Select(l => new WorkingLine(l.Id, l.ProductId, l.Name, l.Quantity, l.UnitPrice, l.LineTotal, l.Notes, l.Status, false))],
        o.Subtotal,
        o.DiscountTotal,
        o.TaxTotal,
        o.Total,
        o.AmountPaid,
        o.BalanceDue,
        o.Total,
        o.Currency,
        false,
        o.PendingApprovalId,
        o.BillState,
        o.BillPrintCount,
        o.BillReopenCount,
        o.AwaitingPayment,
        o.PendingCollected,
        o.Collectable);
}
