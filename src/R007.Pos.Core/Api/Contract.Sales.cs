namespace R007.Pos.Core.Api;

public static class ProductKinds
{
    public const string Food = "FOOD";
    public const string Drink = "DRINK";
    public const string Retail = "RETAIL";
    public const string Ticket = "TICKET";
    public const string Rental = "RENTAL";
    public const string Membership = "MEMBERSHIP";
    public const string Service = "SERVICE";
}

public sealed record PrepRoute(Guid? StationId, string? StationName, string Kind);

/// <summary><c>Price</c> is the API-resolved price for the facility, used for display only (never for totals).</summary>
public sealed record Product(
    Guid Id,
    string? Sku,
    string Name,
    Guid CategoryId,
    string Kind,
    decimal Price,
    string Currency,
    bool? TaxInclusive,
    string? TaxRatePercent,
    decimal? TaxAmount,
    PrepRoute? PrepRoute,
    bool? TrackStock,
    bool Active);

public sealed record Category(Guid Id, Guid? ParentId, string Name, int? SortOrder);

public static class OrderStatuses
{
    public const string Draft = "DRAFT";
    public const string Sent = "SENT";
    public const string InPreparation = "IN_PREPARATION";
    public const string Ready = "READY";
    public const string Served = "SERVED";
    public const string Settled = "SETTLED";
    public const string Voided = "VOIDED";
    public const string PendingApproval = "PENDING_APPROVAL";
}

public static class AdjustmentKinds
{
    public const string DiscountPercent = "DISCOUNT_PERCENT";
    public const string DiscountAmount = "DISCOUNT_AMOUNT";
    public const string PriceOverride = "PRICE_OVERRIDE";
    public const string Comp = "COMP";
}

public sealed record LineAdjustment(Guid Id, string Kind, string Value, string Reason, decimal? Amount, string Status, Guid? ApprovalId);

public sealed record OrderLine(
    Guid Id,
    Guid ProductId,
    string Name,
    int Quantity,
    decimal UnitPrice,
    decimal? TaxAmount,
    decimal LineTotal,
    string? Notes,
    string Status,
    PrepRoute? PrepRoute,
    IReadOnlyList<LineAdjustment>? Adjustments);

/// <summary>
/// Server-priced order. Every total the POS shows is one of these fields; the client performs no
/// price, tax or discount arithmetic. <c>RowVersion</c> drives <c>If-Match</c>.
/// </summary>
public sealed record Order(
    Guid Id,
    string Number,
    Guid FacilityId,
    Guid? TableId,
    Guid? TabId,
    string? CustomerName,
    string? Channel,
    string Status,
    IReadOnlyList<OrderLine> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Total,
    decimal AmountPaid,
    decimal BalanceDue,
    string Currency,
    Guid? PendingApprovalId,
    int RowVersion,
    DateTimeOffset CreatedAt)
{
    public bool IsDraft => Status == OrderStatuses.Draft;

    public bool IsClosed => Status is OrderStatuses.Settled or OrderStatuses.Voided;
}

public sealed record OrderSummary(
    Guid Id,
    string Number,
    Guid FacilityId,
    Guid? TableId,
    string? TableLabel,
    Guid? TabId,
    string Status,
    decimal Total,
    decimal BalanceDue,
    int? LineCount,
    DateTimeOffset? CreatedAt);

public static class OrderChannels
{
    public const string DineIn = "DINE_IN";
    public const string Takeaway = "TAKEAWAY";
    public const string Counter = "COUNTER";
}

public sealed record OrderLineInput(Guid ProductId, int Quantity, string? Notes = null, Guid? Id = null, DateTimeOffset? ClientCreatedAt = null);

public sealed record CreateOrderRequest(
    Guid FacilityId,
    Guid? TableId = null,
    Guid? TabId = null,
    string? Channel = null,
    string? CustomerName = null,
    IReadOnlyList<OrderLineInput>? Lines = null,
    Guid? Id = null,
    DateTimeOffset? ClientCreatedAt = null);

public sealed record SendOrderRequest(IReadOnlyList<Guid>? LineIds = null);

public sealed record VoidRequest(string Reason);

/// <summary><c>Value</c> is a decimal string: a percent, an amount, or the new unit price depending on <c>Kind</c>. The API validates and prices it.</summary>
public sealed record AdjustmentRequest(string Kind, string Value, string Reason);

public static class ApprovalStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
}

public sealed record Approval(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    Guid? FacilityId,
    string Status,
    Guid? RequestedByStaffId,
    string? RequestedByName,
    DateTimeOffset RequestedAt,
    string Reason,
    decimal? Amount,
    string? Summary,
    Guid? DecidedByStaffId,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    string? RequiredPermission)
{
    public bool IsPending => Status == ApprovalStatuses.Pending;
}

/// <summary>HTTP 202 body: the action is held until a supervisor decides.</summary>
public sealed record ApprovalOutcome(string Status, Approval Approval, Order? Order);

public static class ApprovalDecisions
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
}

public sealed record ApprovalDecisionRequest(string Decision, string? Note = null, string? StepUpToken = null);

/// <summary>Outcome of a sensitive order action: applied now (<c>Order</c>) or held for a supervisor (<c>Approval</c>, HTTP 202).</summary>
public sealed record SensitiveOrderResult(Order? Order, ApprovalOutcome? Pending)
{
    public bool NeedsApproval => Pending is not null;
}

public sealed record DiningTable(Guid Id, Guid FacilityId, string Label, int? Seats, string Status, IReadOnlyList<Guid>? OpenOrderIds, Guid? OpenTabId, int? RowVersion);

public sealed record Tab(
    Guid Id,
    Guid FacilityId,
    Guid? TableId,
    string? CustomerName,
    string Status,
    IReadOnlyList<Guid> OrderIds,
    decimal Total,
    decimal AmountPaid,
    decimal BalanceDue,
    string? Currency,
    DateTimeOffset OpenedAt,
    int? RowVersion)
{
    public bool IsOpen => Status == "OPEN";
}

public sealed record OpenTabRequest(Guid FacilityId, Guid? TableId = null, string? CustomerName = null, IReadOnlyList<Guid>? OrderIds = null, Guid? Id = null, DateTimeOffset? ClientCreatedAt = null);

public sealed record AddTabOrdersRequest(IReadOnlyList<Guid> OrderIds);

public sealed record Membership(
    Guid Id,
    string Number,
    Guid PlanId,
    string? PlanName,
    string HolderName,
    string Status,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil);

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);
