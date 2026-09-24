namespace R007.Pos.Core.Api;

// Waiter collection: pre-bill, collected-by-waiter payments awaiting the cashier, cash handover.
// Contract: api/openapi/v1.yaml tags Bills / Collections / Cash handover (all x-additive). The POS is the cashier/supervisor side:
// it never collects at a table and never decides a business rule, it shows what the API says and forwards the cashier's decision.

public static class BillStates
{
    public const string Open = "OPEN";
    public const string BillPrinted = "BILL_PRINTED";
}

/// <summary>The tender a waiter collected (the ledger <c>tenderType</c> on the payment is the mapped one: CARD_TERMINAL becomes POS_TERMINAL, PAY_LINK becomes CARD).</summary>
public static class CollectionTenders
{
    public const string Cash = "CASH";
    public const string CardTerminal = "CARD_TERMINAL";
    public const string Transfer = "TRANSFER";
    public const string PayLink = "PAY_LINK";

    public static string Label(string? tender) => tender switch
    {
        Cash => "Cash",
        CardTerminal => "Card machine",
        Transfer => "Bank transfer",
        PayLink => "Pay link",
        null or "" => "Unknown",
        _ => tender,
    };
}

public static class CollectionDecisions
{
    public const string Confirmed = "CONFIRMED";
    public const string Rejected = "REJECTED";
    public const string Expired = "EXPIRED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>
/// The <c>collection</c> block of a waiter-collected <see cref="Payment"/>. The trailing display fields (<c>OrderNumber</c>, <c>TableLabel</c>,
/// <c>CollectedByName</c>, <c>TerminalLabel</c>) are an additive extension so the cashier's inbox needs no lookups; a node without them still works
/// (the inbox resolves the order/table itself, see <c>CollectionsInboxViewModel</c>).
/// </summary>
public sealed record CollectionInfo(
    string Tender,
    string? Channel,
    Guid? CollectedByStaffId,
    Guid? CollectedDeviceId,
    Guid? TerminalId,
    string? ApprovalCode,
    string? SlipReference,
    string? Last4,
    string? BankReference,
    string? Note,
    DateTimeOffset? ClientCreatedAt,
    DateTimeOffset? ExpiresAt,
    bool? AutoConfirm,
    string? Decision,
    string? ConfirmationMode,
    Guid? DecidedByStaffId,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    string? MatchedReference,
    Guid? OrderId = null,
    string? OrderNumber = null,
    string? TableLabel = null,
    string? CollectedByName = null,
    string? TerminalLabel = null)
{
    /// <summary>Provider-confirmed money (pay link / Paystack transfer): only the server can confirm it, never a person.</summary>
    public bool IsAutoConfirm => AutoConfirm == true;
}

public sealed record ConfirmCollectionRequest(string? MatchedReference = null, string? Note = null);

public sealed record RejectCollectionRequest(string Reason);

/// <summary>Result of <c>POST /payments/{id}/confirm</c>: the captured payment, the settled order and the receipt to print.</summary>
public sealed record ConfirmCollectionResult(Payment Payment, OrderSummary? Order, Guid? ReceiptId);

public sealed record BillRequest(string? Note = null);

public sealed record PreBillFacility(Guid? Id, string? Name);

public sealed record PreBillWaiter(Guid? Id, string? Name);

public sealed record PreBillLine(string Name, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record PreBillTaxLine(string Label, decimal Amount);

public sealed record PayLinkInfo(bool Enabled, string? Reference, string? Url, string? QrPayload);

/// <summary>The 80 mm pre-bill the API produced. It is NOT a receipt; the POS lays it out and prints it, it computes nothing.</summary>
public sealed record PreBill(
    string? Kind,
    string? Title,
    Guid? OrderId,
    string OrderNumber,
    PreBillFacility? Facility,
    string? TableLabel,
    PreBillWaiter? Waiter,
    DateTimeOffset PrintedAt,
    int PrintCount,
    bool Reprint,
    IReadOnlyList<PreBillLine> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    IReadOnlyList<PreBillTaxLine>? TaxLines,
    decimal TaxTotal,
    decimal Total,
    decimal AmountPaid,
    decimal BalanceDue,
    string Currency,
    PayLinkInfo? PayLink,
    string? Disclaimer,
    IReadOnlyList<string>? PrintLines);

/// <summary><c>POST /orders/{id}/bill</c>: the frozen order, the pre-bill and whether this was a reprint (counted and audited).</summary>
public sealed record BillResult(Order Order, PreBill Bill, bool Reprint);

public sealed record CancelBillRequest(string Reason);

/// <summary>HTTP 202 of <c>POST /orders/{id}/bill/cancel</c>: reopening a printed bill is held for a supervisor.</summary>
public sealed record BillCancelPending(Order? Order, Approval Approval);

/// <summary>Outcome of reopening a bill: the reopened order now, or a pending approval (202).</summary>
public sealed record CancelBillResult(Order? Order, BillCancelPending? Pending)
{
    public bool NeedsApproval => Pending is not null;
}

public static class HandoverStatuses
{
    public const string PendingReceipt = "PENDING_RECEIPT";
    public const string Received = "RECEIVED";
    public const string PendingSignoff = "PENDING_SIGNOFF";
    public const string SignedOff = "SIGNED_OFF";
}

/// <summary>A waiter's cash handover to the cashier. <c>Variance</c> is counted minus declared (negative = short); over the facility limit it waits for a supervisor sign-off.</summary>
public sealed record CashHandover(
    Guid Id,
    Guid? FacilityId,
    Guid WaiterStaffId,
    string Status,
    decimal? ExpectedInHand,
    decimal DeclaredAmount,
    decimal? CountedAmount,
    decimal? Variance,
    string? VarianceKind,
    bool? RequiresSignoff,
    string? Note,
    Guid? ReceivedByStaffId,
    DateTimeOffset? ReceivedAt,
    Guid? SignedOffByStaffId,
    DateTimeOffset? SignedOffAt,
    DateTimeOffset CreatedAt,
    string? WaiterName = null)
{
    public bool IsPendingReceipt => Status == HandoverStatuses.PendingReceipt;

    public bool IsPendingSignoff => Status == HandoverStatuses.PendingSignoff;
}

public sealed record ReceiveHandoverRequest(decimal CountedAmount, string? Note = null);

public sealed record SignoffHandoverRequest(string? Note = null);

/// <summary>A waiter's cash position (<c>GET /staff/{id}/cash-in-hand</c>).</summary>
public sealed record CashInHand(
    Guid StaffId,
    string? Currency,
    [property: System.Text.Json.Serialization.JsonPropertyName("cashInHand")] decimal CashInHandAmount,
    decimal? Limit,
    bool? HandoverRequired,
    bool? CashHoldingAllowed,
    DateTimeOffset? OldestUncollectedAt,
    int? PendingCollections,
    decimal? PendingCollectionsAmount,
    int? OpenHandovers,
    decimal? UnsignedShortfall,
    string? WaiterName = null);

/// <summary>Waiter-side request (<c>POST /orders/{id}/collections</c>). The POS never sends it; it exists for the mock server, tests and the harness.</summary>
public sealed record CollectionRequest(
    string TenderType,
    decimal Amount,
    Guid? Id = null,
    decimal? Tendered = null,
    Guid? TerminalId = null,
    string? ApprovalCode = null,
    string? SlipReference = null,
    string? Last4 = null,
    string? BankReference = null,
    string? Note = null);

public sealed record CollectionResult(Payment Payment, OrderSummary? Order, Guid? ReceiptId = null);

/// <summary>Waiter-side request (<c>POST /cash-handovers</c>); for the mock server, tests and the harness.</summary>
public sealed record DeclareHandoverRequest(decimal DeclaredAmount, Guid? Id = null, Guid? FacilityId = null, string? Note = null);
