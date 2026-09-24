namespace R007.Pos.Core.Api;

public static class TenderTypes
{
    public const string Cash = "CASH";
    public const string Card = "CARD";
    public const string Transfer = "TRANSFER";
    public const string PosTerminal = "POS_TERMINAL";

    public static readonly string[] All = [Cash, Card, PosTerminal, Transfer];

    /// <summary>Tenders where staff record an external reference (terminal RRN, transfer reference).</summary>
    public static bool NeedsReference(string tender) => tender is Card or PosTerminal or Transfer;
}

public static class PaymentStatuses
{
    public const string Initiated = "INITIATED";
    public const string Authorizing = "AUTHORIZING";
    public const string Captured = "CAPTURED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string PartiallyRefunded = "PARTIALLY_REFUNDED";
    public const string Refunded = "REFUNDED";
    public const string Reversed = "REVERSED";

    /// <summary>A waiter collected it by hand (card machine slip, cash, transfer): not money in the till until a cashier confirms.</summary>
    public const string PendingConfirmation = "PENDING_CONFIRMATION";
    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
}

public sealed record AllocationInput(Guid OrderId, decimal Amount);

/// <summary>One tender. <c>Tendered</c> is the cash handed over (server computes change). <c>Id</c> is an optional client UUIDv7 (idempotent by id, becomes the Payment id).</summary>
public sealed record TenderInput(
    string TenderType,
    decimal Amount,
    string? Reference = null,
    decimal? Tendered = null,
    Guid? Id = null,
    DateTimeOffset? ClientCreatedAt = null);

/// <summary>Split payment = several tenders in one atomic request. Allocations name the order(s) and amount(s) settled.</summary>
public sealed record CreatePaymentRequest(
    Guid FacilityId,
    IReadOnlyList<AllocationInput> Allocations,
    IReadOnlyList<TenderInput> Tenders,
    Guid? CashSessionId = null,
    Guid? TabId = null,
    string? CustomerName = null,
    DateTimeOffset? ClientCreatedAt = null);

public sealed record SettleTabRequest(IReadOnlyList<TenderInput> Tenders, Guid? CashSessionId = null);

public sealed record PaymentAllocation(Guid OrderId, decimal Amount);

public sealed record Payment(
    Guid Id,
    Guid? GroupId,
    Guid FacilityId,
    string TenderType,
    string? Provider,
    string? ProviderReference,
    string? Reference,
    string Status,
    decimal Amount,
    decimal? Tendered,
    decimal? ChangeGiven,
    decimal? RefundedAmount,
    string Currency,
    IReadOnlyList<PaymentAllocation>? Allocations,
    Guid? CashSessionId,
    Guid? ReceiptId,
    Guid? TakenByStaffId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CapturedAt,
    CollectionInfo? Collection = null)
{
    public bool IsCaptured => Status == PaymentStatuses.Captured;

    public bool IsPendingConfirmation => Status == PaymentStatuses.PendingConfirmation;

    public bool IsAwaitingProvider => Status is PaymentStatuses.Authorizing or PaymentStatuses.Initiated;
}

public sealed record PaymentResult(IReadOnlyList<Payment> Payments, IReadOnlyList<OrderSummary>? Orders, Guid ReceiptId, decimal? ChangeDue);

public sealed record RefundRequest(decimal Amount, string Reason, string? TenderType = null);

public sealed record Refund(Guid Id, Guid PaymentId, decimal Amount, string? Reason, string Status, Guid? ApprovalId, DateTimeOffset? CreatedAt)
{
    public bool IsPendingApproval => Status == "PENDING_APPROVAL";
}

public sealed record ReversalRequest(string Reason);

public sealed record PaymentReversal(Guid Id, Guid PaymentId, string? Reason, string Status, Guid? ApprovalId, DateTimeOffset? CreatedAt)
{
    public bool IsPendingApproval => Status == "PENDING_APPROVAL";
}

/// <summary>Paystack pay-link. The customer pays on <c>AuthorizationUrl</c>; the POS then verifies by <c>Reference</c>.</summary>
public sealed record PaystackInitRequest(decimal Amount, string Email, IReadOnlyList<Guid>? OrderIds = null, Guid? BookingId = null, Guid? MembershipId = null, string? CallbackUrl = null);

public sealed record PaystackInitResult(Guid PaymentId, string Reference, string AuthorizationUrl, string? AccessCode);

public sealed record ReceiptItem(string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record ReceiptTender(string TenderType, decimal Amount, string? Reference, decimal? Tendered = null);

/// <summary>Structured receipt from the API. The POS lays it out for the 80 mm printer; it does not compute any amount.</summary>
public sealed record Receipt(
    Guid Id,
    string Number,
    Guid? FacilityId,
    string FacilityName,
    string? SiteName,
    string? SiteAddress,
    DateTimeOffset IssuedAt,
    string? CashierName,
    IReadOnlyList<string>? OrderNumbers,
    string? TableLabel,
    IReadOnlyList<ReceiptItem> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Total,
    string Currency,
    IReadOnlyList<ReceiptTender> Tenders,
    decimal? ChangeGiven,
    string? VatNumber,
    string? QrPayload,
    int? ReprintCount,
    string? Footer,
    decimal? AmountPaid = null,
    decimal? BalanceDue = null,
    bool? Duplicate = null,
    string? BusinessName = null,
    IReadOnlyList<string>? PrintLines = null,
    string? Terminal = null);

public sealed record CashSession(
    Guid Id,
    Guid FacilityId,
    Guid StaffId,
    string Status,
    decimal OpeningFloat,
    decimal? ExpectedCash,
    decimal? CountedCash,
    decimal? Variance,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    CashSessionTotals? Totals = null)
{
    public bool IsOpen => Status == "OPEN";
}

/// <summary>Running totals the node keeps on the session (what a cashier may see without <c>report.view</c>).</summary>
public sealed record CashSessionTotals(
    decimal? CashSales,
    decimal? CashRefunds,
    decimal? CashReversals,
    decimal? PaidIn,
    decimal? PaidOut,
    decimal? Drops,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(R007.Pos.Core.Money.DecimalMapJsonConverter))] IReadOnlyDictionary<string, decimal>? NonCash);

public sealed record OpenCashSessionRequest(Guid FacilityId, decimal OpeningFloat);

public sealed record CloseCashSessionRequest(decimal CountedCash, string? Note = null);

public sealed record ShiftTenderTotal(string TenderType, decimal Amount, int? Count);

/// <summary>Every report response says how fresh it is (local node vs. cloud copy); the POS surfaces stale data instead of hiding it.</summary>
public sealed record Freshness(DateTimeOffset GeneratedAt, string SourceNode, DateTimeOffset? LastSyncAt, bool Stale, string? StaleReason, int? AgeSeconds, int? StaleAfterSeconds);

public sealed record CashierShiftReport(
    Guid ShiftId,
    Guid StaffId,
    string? StaffName,
    Guid FacilityId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal OpeningFloat,
    IReadOnlyList<ShiftTenderTotal> ByTender,
    decimal ExpectedCash,
    decimal? CountedCash,
    decimal? Variance,
    decimal? Refunds,
    int? Voids,
    Freshness Freshness);
