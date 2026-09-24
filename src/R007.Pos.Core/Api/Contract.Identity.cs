namespace R007.Pos.Core.Api;

// Wire models mirroring api/openapi/v1.yaml (007resort-docs / contract). camelCase JSON, money as decimal strings,
// UUIDv7 ids. Statuses and kinds stay strings on purpose: an unknown value from a newer API must not break parsing
// (contract README: "treat unknown enum values as other").

/// <summary>Where the node's Reverb (Pusher protocol) server is (from <c>GET /system/info</c>).</summary>
public sealed record RealtimeInfo(string Scheme, string Host, int Port, string AppKey);

public sealed record SystemInfo(
    string Service,
    string ApiVersion,
    string DeploymentMode,
    Guid? NodeId,
    DateTimeOffset ServerTime,
    string? Timezone,
    string Currency,
    IReadOnlyDictionary<string, string>? MinClientVersion,
    bool? VatEnabled,
    RealtimeInfo? Realtime = null);

/// <summary>Pusher-style private channel authorisation (<c>POST /broadcasting/auth</c>; property names are snake_case on the wire).</summary>
public sealed record BroadcastAuthRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("socket_id")] string SocketId,
    [property: System.Text.Json.Serialization.JsonPropertyName("channel_name")] string ChannelName);

public sealed record BroadcastAuthResponse(string Auth);

public sealed record Staff(
    Guid Id,
    string DisplayName,
    string? StaffNumber,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<Guid>? FacilityIds)
{
    public bool Has(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);
}

public sealed record SessionRef(Guid Id, DateTimeOffset? ExpiresAt, Guid? DeviceId);

public static class CredentialTypes
{
    public const string Password = "PASSWORD";
    public const string Pin = "PIN";
    public const string NfcCard = "NFC_CARD";
}

/// <summary>For <c>NFC_CARD</c> the <c>Identifier</c> is the card UID and the <c>Secret</c> is the staff PIN (the node never accepts a card alone).</summary>
public sealed record StaffLoginRequest(string CredentialType, string? Identifier, string Secret);

public sealed record AuthResult(string AccessToken, string RefreshToken, int ExpiresInSeconds, Staff Staff, SessionRef? Session);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(bool? AllSessions = null);

/// <summary>Supervisor re-authentication for one sensitive action; yields a single-use token for <c>X-Step-Up-Token</c>.</summary>
public sealed record StepUpRequest(string CredentialType, string? Identifier, string Secret, string Permission, string? EntityType = null, Guid? EntityId = null);

public sealed record StepUpApprover(Guid Id, string DisplayName);

public sealed record StepUpResult(string StepUpToken, int ExpiresInSeconds, StepUpApprover? Approver);

public static class DeviceKinds
{
    public const string PosTerminal = "POS_TERMINAL";

    /// <summary>The explicit device <c>mode</c> the node keeps next to <c>kind</c> (POS terminals are mode POS).</summary>
    public const string PosMode = "POS";
}

public sealed record DeviceRegisterRequest(string Name, string Kind, string HardwareId, string RegistrationCode, string? Platform = null, string? AppVersion = null, string? Mode = null);

public sealed record Device(
    Guid Id,
    string Name,
    string Kind,
    string Status,
    Guid? FacilityId,
    Guid? HomeFacilityId);

public sealed record DeviceRegisterResult(Device Device, string DeviceToken);

public sealed record Facility(Guid Id, Guid SiteId, string Code, string Name, string Kind, string Status);

public sealed record OperatingRules(
    decimal? ApprovalThresholdAmount,
    IReadOnlyList<string>? RequireApprovalFor,
    bool? AllowOpenTabs,
    bool? RequireCashSession,
    bool? AllowOfflineOrders,
    string? AllowOfflinePayments,
    int? HoldTtlSeconds,
    bool? VatEnabled,
    string? VatRatePercent,
    string? PaymentTiming = null,
    bool? WaiterCollectionEnabled = null,
    bool? WaiterCashHolding = null,
    int? PendingCollectionExpiryMinutes = null)
{
    /// <summary>Reception-style counter: the order is paid BEFORE service (a DRAFT order may be settled).</summary>
    public bool IsPayFirst => string.Equals(PaymentTiming, PaymentTimings.PayFirst, StringComparison.Ordinal);

    /// <summary>Restaurant-style: only a SERVED order can be paid (<c>PAY_AFTER_SERVICE</c>, alias <c>PAY_BEFORE_LEAVING</c>; tabs settle on exit).</summary>
    public bool IsPayAfterService => !IsPayFirst && PaymentTiming is not null;
}

/// <summary>Values of the <c>paymentTiming</c> operating rule as the node emits them (aliases included).</summary>
public static class PaymentTimings
{
    public const string PayFirst = "PAY_FIRST";
    public const string PayAfterService = "PAY_AFTER_SERVICE";
    public const string PayBeforeLeaving = "PAY_BEFORE_LEAVING";
    public const string OpenTab = "OPEN_TAB";
}

public sealed record FacilityCapabilities(Guid FacilityId, IReadOnlyList<string> Capabilities, OperatingRules? OperatingRules)
{
    public bool Has(string capability) => Capabilities.Contains(capability, StringComparer.Ordinal);
}

/// <summary>Capability codes (architecture/05) the POS reacts to.</summary>
public static class Capabilities
{
    public const string Pos = "POS";
    public const string TableService = "TABLE_SERVICE";
    public const string OpenTab = "OPEN_TAB";
    public const string KitchenRouting = "KITCHEN_ROUTING";
    public const string BarRouting = "BAR_ROUTING";
    public const string Ticketing = "TICKETING";
    public const string Booking = "BOOKING";
    public const string BarcodeSales = "BARCODE_SALES";
    public const string ReceiptPrinting = "RECEIPT_PRINTING";
    public const string PaymentAcceptance = "PAYMENT_ACCEPTANCE";
    public const string EquipmentRental = "EQUIPMENT_RENTAL";
    public const string Membership = "MEMBERSHIP";
}

public static class OfflinePaymentPolicy
{
    public const string None = "NONE";
    public const string CashOnly = "CASH_ONLY";
    public const string All = "ALL";
}

/// <summary>Permission codes (architecture/06 + OpenAPI) the POS gates UI on. The API remains the enforcer.</summary>
public static class Permissions
{
    public const string OrderCreate = "order.create";
    public const string OrderView = "order.view";
    public const string OrderLineAdd = "order.line.add";
    public const string OrderLineRemoveUnsent = "order.line.remove_unsent";
    public const string OrderSend = "order.send";
    public const string OrderServe = "order.serve";
    public const string OrderVoidExecute = "order.void.execute";
    public const string OrderVoidApprove = "order.void.approve";
    public const string OrderDiscountExecute = "order.discount.execute";
    public const string OrderDiscountApprove = "order.discount.approve";
    public const string OrderCompExecute = "order.comp.execute";
    public const string OrderCompApprove = "order.comp.approve";
    public const string OrderPriceOverrideExecute = "order.price_override.execute";
    public const string OrderPriceOverrideApprove = "order.price_override.approve";
    public const string OrderSettle = "order.settle";
    public const string PaymentTake = "payment.take";
    public const string PaymentSplit = "payment.split";
    public const string PaymentView = "payment.view";
    public const string RefundExecute = "refund.execute";
    public const string RefundApprove = "refund.approve";
    public const string PaymentReversalExecute = "payment.reversal.execute";
    public const string PaymentReversalApprove = "payment.reversal.approve";
    public const string CashSessionOpen = "cash_session.open";
    public const string CashSessionClose = "cash_session.close";
    public const string CashSessionView = "cash_session.view";
    public const string ReceiptView = "receipt.view";
    public const string ReceiptReprint = "receipt.reprint";
    public const string TabView = "tab.view_own_facility";
    public const string TabOpen = "tab.open";
    public const string BookingCreate = "booking.create";
    public const string TicketIssue = "ticket.issue";
    public const string MembershipView = "membership.view";
    public const string ReportView = "report.view";

    // Waiter collection (bill, confirm, cash handover)
    public const string BillPrint = "bill.print";
    public const string BillCancelExecute = "bill.cancel.execute";
    public const string BillCancelApprove = "bill.cancel.approve";
    public const string PaymentConfirm = "payment.confirm";
    public const string CashHandoverView = "cash_handover.view";
    public const string CashHandoverReceive = "cash_handover.receive";
    public const string CashHandoverSignoff = "cash_handover.signoff";

    /// <summary>Any of these lets a staff member decide approvals on this terminal.</summary>
    public static readonly string[] ApprovalDecisions =
    [
        OrderVoidApprove, OrderDiscountApprove, OrderCompApprove, OrderPriceOverrideApprove,
        RefundApprove, PaymentReversalApprove,
    ];
}
