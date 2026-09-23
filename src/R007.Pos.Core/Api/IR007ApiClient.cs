namespace R007.Pos.Core.Api;

/// <summary>
/// Typed client for the 007 Resort &amp; Spa API (contract: <c>api/openapi/v1.yaml</c>). The POS is a thin client:
/// every business decision (pricing, tax, permissions, stock, entitlements) is made by the API.
/// <para>Conventions: every mutating call takes an explicit <c>idempotencyKey</c> that the caller generates once per
/// user intent (<see cref="IdempotencyKeys"/>) and reuses if that intent is retried. Aggregates with a
/// <c>rowVersion</c> take it as <c>rowVersion</c> and are sent as <c>If-Match: "v{n}"</c>. Sensitive calls accept an
/// optional step-up token (sent as <c>X-Step-Up-Token</c>).</para>
/// </summary>
public interface IR007ApiClient
{
    // System / device ---------------------------------------------------------------------------------------
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>Cheap liveness probe (<c>GET /health/live</c>); false on any failure.</summary>
    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    Task<DeviceRegisterResult> RegisterDeviceAsync(DeviceRegisterRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<FacilityCapabilities> GetCapabilitiesAsync(Guid facilityId, CancellationToken cancellationToken = default);

    Task<Facility> GetFacilityAsync(Guid facilityId, CancellationToken cancellationToken = default);

    // Auth --------------------------------------------------------------------------------------------------
    Task<AuthResult> LoginAsync(StaffLoginRequest request, CancellationToken cancellationToken = default);

    Task LogoutAsync(CancellationToken cancellationToken = default);

    Task<StepUpResult> StepUpAsync(StepUpRequest request, CancellationToken cancellationToken = default);

    // Catalog -----------------------------------------------------------------------------------------------
    Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    /// <summary>All products for the facility (pages followed). <paramref name="query"/> searches name/sku/barcode server-side.</summary>
    Task<IReadOnlyList<Product>> GetProductsAsync(Guid facilityId, string? query = null, CancellationToken cancellationToken = default);

    // Tables & tabs -----------------------------------------------------------------------------------------
    Task<IReadOnlyList<DiningTable>> GetTablesAsync(Guid facilityId, CancellationToken cancellationToken = default);

    Task<DiningTable> OpenTableAsync(Guid tableId, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Tab>> ListOpenTabsAsync(Guid facilityId, CancellationToken cancellationToken = default);

    Task<Tab> GetTabAsync(Guid tabId, CancellationToken cancellationToken = default);

    Task<Tab> OpenTabAsync(OpenTabRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Tab> AddOrdersToTabAsync(Guid tabId, int rowVersion, AddTabOrdersRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Settle a tab on exit with one or more tenders (split allowed).</summary>
    Task<PaymentResult> SettleTabAsync(Guid tabId, SettleTabRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    // Orders ------------------------------------------------------------------------------------------------
    Task<Order> CreateOrderAsync(CreateOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Order> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<Page<OrderSummary>> ListOrdersAsync(Guid facilityId, string? statuses = null, Guid? tabId = null, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default);

    Task<Order> AddLineAsync(Guid orderId, int rowVersion, OrderLineInput line, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Order> RemoveLineAsync(Guid orderId, Guid lineId, int rowVersion, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Order> SendOrderAsync(Guid orderId, int rowVersion, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Order> ServeOrderAsync(Guid orderId, int rowVersion, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<SensitiveOrderResult> VoidOrderAsync(Guid orderId, int rowVersion, VoidRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default);

    Task<SensitiveOrderResult> AdjustLineAsync(Guid orderId, Guid lineId, int rowVersion, AdjustmentRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default);

    // Approvals ---------------------------------------------------------------------------------------------
    Task<Approval> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default);

    /// <summary><paramref name="scope"/> is <c>mine</c> or <c>approvable</c>.</summary>
    Task<IReadOnlyList<Approval>> ListApprovalsAsync(string scope, string? status = ApprovalStatuses.Pending, CancellationToken cancellationToken = default);

    Task<Approval> DecideApprovalAsync(Guid approvalId, ApprovalDecisionRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Approval> CancelApprovalAsync(Guid approvalId, string idempotencyKey, CancellationToken cancellationToken = default);

    // Payments ----------------------------------------------------------------------------------------------
    Task<PaymentResult> CreatePaymentAsync(CreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Payment> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default);

    Task<Page<Payment>> ListPaymentsAsync(Guid facilityId, Guid? cashSessionId = null, string? cursor = null, int limit = 25, CancellationToken cancellationToken = default);

    Task<Refund> RefundPaymentAsync(Guid paymentId, RefundRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default);

    Task<PaymentReversal> ReversePaymentAsync(Guid paymentId, ReversalRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default);

    Task<PaystackInitResult> PaystackInitializeAsync(PaystackInitRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Payment> PaystackVerifyAsync(string reference, CancellationToken cancellationToken = default);

    // Receipts ----------------------------------------------------------------------------------------------
    /// <summary><paramref name="reprint"/> increments the reprint count and marks the copy DUPLICATE (needs <c>receipt.reprint</c>).</summary>
    Task<Receipt> GetReceiptAsync(Guid receiptId, bool reprint = false, CancellationToken cancellationToken = default);

    Task<Receipt> GetOrderReceiptAsync(Guid orderId, CancellationToken cancellationToken = default);

    // Memberships (customer lookup) -------------------------------------------------------------------------
    Task<IReadOnlyList<Membership>> SearchMembershipsAsync(string query, CancellationToken cancellationToken = default);

    // Cash sessions & reports -------------------------------------------------------------------------------
    Task<CashSession?> GetOpenCashSessionAsync(Guid facilityId, Guid staffId, CancellationToken cancellationToken = default);

    Task<CashSession> OpenCashSessionAsync(OpenCashSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<CashSession> CloseCashSessionAsync(Guid sessionId, CloseCashSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<CashierShiftReport> GetShiftReportAsync(Guid sessionId, CancellationToken cancellationToken = default);

    // Reception: bookings, tickets, rentals -----------------------------------------------------------------
    Task<IReadOnlyList<BookableResource>> GetBookableResourcesAsync(CancellationToken cancellationToken = default);

    Task<Availability> GetAvailabilityAsync(Guid resourceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<Booking> HoldBookingAsync(HoldRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Booking> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken = default);

    Task<Booking> ConfirmBookingAsync(Guid bookingId, int rowVersion, ConfirmBookingRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Booking> CancelBookingAsync(Guid bookingId, int rowVersion, CancelBookingRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    Task<Entitlement> GetEntitlementAsync(Guid entitlementId, CancellationToken cancellationToken = default);

    Task<Entitlement> IssueEntitlementAsync(IssueEntitlementRequest request, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>Generates <c>Idempotency-Key</c> values (time-ordered GUIDs, &gt;= 16 chars as the contract requires) and client ids.</summary>
public static class IdempotencyKeys
{
    public static string New() => Guid.CreateVersion7().ToString("D");
}

/// <summary>Client-generated UUIDv7 ids for offline-creatable resources (orders, lines, tabs, tenders).</summary>
public static class ClientIds
{
    public static Guid New() => Guid.CreateVersion7();
}
