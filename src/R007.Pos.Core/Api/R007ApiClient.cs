using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace R007.Pos.Core.Api;

/// <summary>
/// <see cref="HttpClient"/>-based implementation of <see cref="IR007ApiClient"/>. Authentication headers, retries
/// and token refresh live in the <see cref="HttpClient"/>'s handler pipeline (see <c>Http/</c>), so this class only
/// maps operations to endpoints, sets <c>Idempotency-Key</c>, and turns problem+json into <see cref="ApiException"/>.
/// The <see cref="HttpClient.BaseAddress"/> must point at the site API (e.g. <c>http://localhost:5080/</c>).
/// </summary>
public sealed class R007ApiClient(HttpClient httpClient) : IR007ApiClient
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string StepUpHeader = "X-Step-Up-Token";

    private static PosJsonContext Ctx => PosJsonContext.Default;

    private static string Id(Guid id) => id.ToString("D");

    private static string Q(string value) => Uri.EscapeDataString(value);

    // System / device ---------------------------------------------------------------------------------------
    public Task<SystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
        GetAsync("api/v1/system/info", Ctx.SystemInfo, cancellationToken);

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendRawAsync(HttpMethod.Get, "api/v1/health/live", null, null, null, null, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return false;
        }
    }

    public Task<DeviceRegisterResult> RegisterDeviceAsync(DeviceRegisterRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/devices/register", request, Ctx.DeviceRegisterRequest, Ctx.DeviceRegisterResult, idempotencyKey, cancellationToken: cancellationToken);

    public Task<FacilityCapabilities> GetCapabilitiesAsync(Guid facilityId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/facilities/{Id(facilityId)}/capabilities", Ctx.FacilityCapabilities, cancellationToken);

    public Task<Facility> GetFacilityAsync(Guid facilityId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/organization/facilities/{Id(facilityId)}", Ctx.Facility, cancellationToken);

    // Auth --------------------------------------------------------------------------------------------------
    public Task<AuthResult> LoginAsync(StaffLoginRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/auth/staff/login", request, Ctx.StaffLoginRequest, Ctx.AuthResult, null, cancellationToken: cancellationToken);

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var content = JsonContent(new LogoutRequest(), Ctx.LogoutRequest);
        using var response = await SendRawAsync(HttpMethod.Post, "api/v1/auth/staff/logout", content, null, null, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<StepUpResult> StepUpAsync(StepUpRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/auth/staff/step-up", request, Ctx.StepUpRequest, Ctx.StepUpResult, null, cancellationToken: cancellationToken);

    // Catalog -----------------------------------------------------------------------------------------------
    public Task<IReadOnlyList<Category>> GetCategoriesAsync(CancellationToken cancellationToken = default) =>
        GetAllAsync("api/v1/catalog/categories?limit=200", Ctx.PageCategory, cancellationToken);

    public Task<IReadOnlyList<Product>> GetProductsAsync(Guid facilityId, string? query = null, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/catalog/products?facilityId={Id(facilityId)}&limit=200";
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&q={Q(query)}";
        }

        return GetAllAsync(path, Ctx.PageProduct, cancellationToken);
    }

    // Tables & tabs -----------------------------------------------------------------------------------------
    public Task<IReadOnlyList<DiningTable>> GetTablesAsync(Guid facilityId, CancellationToken cancellationToken = default) =>
        GetAllAsync($"api/v1/tables?facilityId={Id(facilityId)}&limit=200", Ctx.PageDiningTable, cancellationToken);

    public Task<DiningTable> OpenTableAsync(Guid tableId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendNoBodyAsync(HttpMethod.Post, $"api/v1/tables/{Id(tableId)}/open", Ctx.DiningTable, idempotencyKey, null, cancellationToken);

    public Task<IReadOnlyList<Tab>> ListOpenTabsAsync(Guid facilityId, CancellationToken cancellationToken = default) =>
        GetAllAsync($"api/v1/tabs?filter[facilityId]={Id(facilityId)}&filter[status]=OPEN&limit=100", Ctx.PageTab, cancellationToken);

    public Task<Tab> GetTabAsync(Guid tabId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/tabs/{Id(tabId)}", Ctx.Tab, cancellationToken);

    public Task<Tab> OpenTabAsync(OpenTabRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/tabs", request, Ctx.OpenTabRequest, Ctx.Tab, idempotencyKey, cancellationToken: cancellationToken);

    public Task<Tab> AddOrdersToTabAsync(Guid tabId, int rowVersion, AddTabOrdersRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/tabs/{Id(tabId)}/orders", request, Ctx.AddTabOrdersRequest, Ctx.Tab, idempotencyKey, ifMatch: ETag(rowVersion), cancellationToken: cancellationToken);

    public Task<PaymentResult> SettleTabAsync(Guid tabId, SettleTabRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/tabs/{Id(tabId)}/settle", request, Ctx.SettleTabRequest, Ctx.PaymentResult, idempotencyKey, cancellationToken: cancellationToken);

    // Orders ------------------------------------------------------------------------------------------------
    public Task<Order> CreateOrderAsync(CreateOrderRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/orders", request, Ctx.CreateOrderRequest, Ctx.Order, idempotencyKey, cancellationToken: cancellationToken);

    public Task<Order> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/orders/{Id(orderId)}", Ctx.Order, cancellationToken);

    public Task<Page<OrderSummary>> ListOrdersAsync(Guid facilityId, string? statuses = null, Guid? tabId = null, string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/orders?filter[facilityId]={Id(facilityId)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(statuses))
        {
            path += $"&filter[status]={Q(statuses)}";
        }

        if (tabId is { } tab)
        {
            path += $"&filter[tabId]={Id(tab)}";
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            path += $"&cursor={Q(cursor)}";
        }

        return GetAsync(path, Ctx.PageOrderSummary, cancellationToken);
    }

    public Task<Order> AddLineAsync(Guid orderId, int rowVersion, OrderLineInput line, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/orders/{Id(orderId)}/lines", line, Ctx.OrderLineInput, Ctx.Order, idempotencyKey, ifMatch: ETag(rowVersion), cancellationToken: cancellationToken);

    public async Task<Order> RemoveLineAsync(Guid orderId, Guid lineId, int rowVersion, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var response = await SendRawAsync(HttpMethod.Delete, $"api/v1/orders/{Id(orderId)}/lines/{Id(lineId)}", null, idempotencyKey, null, ETag(rowVersion), cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, Ctx.Order, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Order> SendOrderAsync(Guid orderId, int rowVersion, string idempotencyKey, CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Post, $"api/v1/orders/{Id(orderId)}/send", new SendOrderRequest(), Ctx.SendOrderRequest, Ctx.Order, idempotencyKey, ifMatch: ETag(rowVersion), cancellationToken: cancellationToken).ConfigureAwait(false);

    public Task<Order> ServeOrderAsync(Guid orderId, int rowVersion, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendNoBodyAsync(HttpMethod.Post, $"api/v1/orders/{Id(orderId)}/serve", Ctx.Order, idempotencyKey, ETag(rowVersion), cancellationToken);

    public Task<SensitiveOrderResult> VoidOrderAsync(Guid orderId, int rowVersion, VoidRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default) =>
        SendSensitiveAsync($"api/v1/orders/{Id(orderId)}/void", request, Ctx.VoidRequest, idempotencyKey, stepUpToken, ETag(rowVersion), cancellationToken);

    public Task<SensitiveOrderResult> AdjustLineAsync(Guid orderId, Guid lineId, int rowVersion, AdjustmentRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default) =>
        SendSensitiveAsync($"api/v1/orders/{Id(orderId)}/lines/{Id(lineId)}/adjustments", request, Ctx.AdjustmentRequest, idempotencyKey, stepUpToken, ETag(rowVersion), cancellationToken);

    // Approvals ---------------------------------------------------------------------------------------------
    public Task<Approval> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/approvals/{Id(approvalId)}", Ctx.Approval, cancellationToken);

    public Task<IReadOnlyList<Approval>> ListApprovalsAsync(string scope, string? status = ApprovalStatuses.Pending, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/approvals?scope={Q(scope)}&limit=100";
        if (!string.IsNullOrEmpty(status))
        {
            path += $"&filter[status]={Q(status)}";
        }

        return GetAllAsync(path, Ctx.PageApproval, cancellationToken);
    }

    public Task<Approval> DecideApprovalAsync(Guid approvalId, ApprovalDecisionRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/approvals/{Id(approvalId)}/decision", request, Ctx.ApprovalDecisionRequest, Ctx.Approval, idempotencyKey, cancellationToken: cancellationToken);

    public Task<Approval> CancelApprovalAsync(Guid approvalId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendNoBodyAsync(HttpMethod.Post, $"api/v1/approvals/{Id(approvalId)}/cancel", Ctx.Approval, idempotencyKey, null, cancellationToken);

    // Payments ----------------------------------------------------------------------------------------------
    public Task<PaymentResult> CreatePaymentAsync(CreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/payments", request, Ctx.CreatePaymentRequest, Ctx.PaymentResult, idempotencyKey, cancellationToken: cancellationToken);

    public Task<Payment> GetPaymentAsync(Guid paymentId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/payments/{Id(paymentId)}", Ctx.Payment, cancellationToken);

    public Task<Page<Payment>> ListPaymentsAsync(Guid facilityId, Guid? cashSessionId = null, string? cursor = null, int limit = 25, CancellationToken cancellationToken = default)
    {
        var path = $"api/v1/payments?filter[facilityId]={Id(facilityId)}&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        if (cashSessionId is { } session)
        {
            path += $"&filter[cashSessionId]={Id(session)}";
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            path += $"&cursor={Q(cursor)}";
        }

        return GetAsync(path, Ctx.PagePayment, cancellationToken);
    }

    public Task<Refund> RefundPaymentAsync(Guid paymentId, RefundRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/payments/{Id(paymentId)}/refund", request, Ctx.RefundRequest, Ctx.Refund, idempotencyKey, stepUpToken, cancellationToken: cancellationToken);

    public Task<PaymentReversal> ReversePaymentAsync(Guid paymentId, ReversalRequest request, string idempotencyKey, string? stepUpToken = null, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/payments/{Id(paymentId)}/reversal", request, Ctx.ReversalRequest, Ctx.PaymentReversal, idempotencyKey, stepUpToken, cancellationToken: cancellationToken);

    public Task<PaystackInitResult> PaystackInitializeAsync(PaystackInitRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/payments/paystack/initialize", request, Ctx.PaystackInitRequest, Ctx.PaystackInitResult, idempotencyKey, cancellationToken: cancellationToken);

    public Task<Payment> PaystackVerifyAsync(string reference, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/payments/paystack/verify/{Q(reference)}", Ctx.Payment, cancellationToken);

    // Receipts ----------------------------------------------------------------------------------------------
    public Task<Receipt> GetReceiptAsync(Guid receiptId, bool reprint = false, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/receipts/{Id(receiptId)}{(reprint ? "?reprint=true" : string.Empty)}", Ctx.Receipt, cancellationToken);

    public Task<Receipt> GetOrderReceiptAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/orders/{Id(orderId)}/receipt", Ctx.Receipt, cancellationToken);

    // Memberships -------------------------------------------------------------------------------------------
    public async Task<IReadOnlyList<Membership>> SearchMembershipsAsync(string query, CancellationToken cancellationToken = default) =>
        (await GetAsync($"api/v1/memberships?q={Q(query)}&limit=25", Ctx.PageMembership, cancellationToken).ConfigureAwait(false)).Items;

    // Cash sessions & reports -------------------------------------------------------------------------------
    public async Task<CashSession?> GetOpenCashSessionAsync(Guid facilityId, Guid staffId, CancellationToken cancellationToken = default)
    {
        var page = await GetAsync(
            $"api/v1/cash-sessions?filter[facilityId]={Id(facilityId)}&filter[status]=OPEN&filter[staffId]={Id(staffId)}&limit=1",
            Ctx.PageCashSession,
            cancellationToken).ConfigureAwait(false);
        return page.Items.Count > 0 ? page.Items[0] : null;
    }

    public Task<CashSession> OpenCashSessionAsync(OpenCashSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/cash-sessions", request, Ctx.OpenCashSessionRequest, Ctx.CashSession, idempotencyKey, cancellationToken: cancellationToken);

    public Task<CashSession> CloseCashSessionAsync(Guid sessionId, CloseCashSessionRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/cash-sessions/{Id(sessionId)}/close", request, Ctx.CloseCashSessionRequest, Ctx.CashSession, idempotencyKey, cancellationToken: cancellationToken);

    public Task<CashierShiftReport> GetShiftReportAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/reports/cashier-shift/{Id(sessionId)}", Ctx.CashierShiftReport, cancellationToken);

    // Reception ---------------------------------------------------------------------------------------------
    public Task<IReadOnlyList<BookableResource>> GetBookableResourcesAsync(CancellationToken cancellationToken = default) =>
        GetAllAsync("api/v1/bookings/resources?limit=100", Ctx.PageBookableResource, cancellationToken);

    public Task<Availability> GetAvailabilityAsync(Guid resourceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
        GetAsync(
            $"api/v1/bookings/resources/{Id(resourceId)}/availability?from={Q(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}&to={Q(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}",
            Ctx.Availability,
            cancellationToken);

    public Task<Booking> HoldBookingAsync(HoldRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/bookings/hold", request, Ctx.HoldRequest, Ctx.Booking, idempotencyKey, cancellationToken: cancellationToken);

    public Task<Booking> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/bookings/{Id(bookingId)}", Ctx.Booking, cancellationToken);

    public Task<Booking> ConfirmBookingAsync(Guid bookingId, int rowVersion, ConfirmBookingRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/bookings/{Id(bookingId)}/confirm", request, Ctx.ConfirmBookingRequest, Ctx.Booking, idempotencyKey, ifMatch: ETag(rowVersion), cancellationToken: cancellationToken);

    public Task<Booking> CancelBookingAsync(Guid bookingId, int rowVersion, CancelBookingRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"api/v1/bookings/{Id(bookingId)}/cancel", request, Ctx.CancelBookingRequest, Ctx.Booking, idempotencyKey, ifMatch: ETag(rowVersion), cancellationToken: cancellationToken);

    public Task<Entitlement> GetEntitlementAsync(Guid entitlementId, CancellationToken cancellationToken = default) =>
        GetAsync($"api/v1/entitlements/{Id(entitlementId)}", Ctx.Entitlement, cancellationToken);

    public Task<Entitlement> IssueEntitlementAsync(IssueEntitlementRequest request, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "api/v1/entitlements", request, Ctx.IssueEntitlementRequest, Ctx.Entitlement, idempotencyKey, cancellationToken: cancellationToken);

    // Plumbing ----------------------------------------------------------------------------------------------
    private static ByteArrayContent JsonContent<T>(T value, JsonTypeInfo<T> info)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, info));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>Formats a <c>rowVersion</c> as the strong ETag the API issues (<c>"v3"</c>).</summary>
    internal static string ETag(int rowVersion) => $"\"v{rowVersion.ToString(CultureInfo.InvariantCulture)}\"";

    private async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> info, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, path, null, null, null, null, ct).ConfigureAwait(false);
        return await ReadAsync(response, info, ct).ConfigureAwait(false);
    }

    /// <summary>Follows <c>nextCursor</c> until exhausted (bounded, to stop a misbehaving server looping us forever).</summary>
    private async Task<IReadOnlyList<T>> GetAllAsync<T>(string path, JsonTypeInfo<Page<T>> info, CancellationToken ct)
    {
        var all = new List<T>();
        string? cursor = null;
        for (var i = 0; i < 50; i++)
        {
            var page = await GetAsync(cursor is null ? path : $"{path}&cursor={Q(cursor)}", info, ct).ConfigureAwait(false);
            all.AddRange(page.Items);
            if (string.IsNullOrEmpty(page.NextCursor))
            {
                break;
            }

            cursor = page.NextCursor;
        }

        return all;
    }

    private async Task<TResp> SendAsync<TReq, TResp>(
        HttpMethod method,
        string path,
        TReq request,
        JsonTypeInfo<TReq> requestInfo,
        JsonTypeInfo<TResp> responseInfo,
        string? idempotencyKey,
        string? stepUpToken = null,
        string? ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent(request, requestInfo);
        using var response = await SendRawAsync(method, path, content, idempotencyKey, stepUpToken, ifMatch, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, responseInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResp> SendNoBodyAsync<TResp>(HttpMethod method, string path, JsonTypeInfo<TResp> responseInfo, string idempotencyKey, string? ifMatch, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, path, null, idempotencyKey, null, ifMatch, ct).ConfigureAwait(false);
        return await ReadAsync(response, responseInfo, ct).ConfigureAwait(false);
    }

    private async Task<SensitiveOrderResult> SendSensitiveAsync<TReq>(string path, TReq request, JsonTypeInfo<TReq> requestInfo, string key, string? stepUp, string ifMatch, CancellationToken ct)
    {
        using var content = JsonContent(request, requestInfo);
        using var response = await SendRawAsync(HttpMethod.Post, path, content, key, stepUp, ifMatch, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            return new SensitiveOrderResult(null, await ReadAsync(response, Ctx.ApprovalOutcome, ct).ConfigureAwait(false));
        }

        return new SensitiveOrderResult(await ReadAsync(response, Ctx.Order, ct).ConfigureAwait(false), null);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, HttpContent? content, string? idempotencyKey, string? stepUpToken, string? ifMatch, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", Guid.CreateVersion7().ToString("D"));
        if (content is not null)
        {
            request.Content = content;
        }

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        }

        if (!string.IsNullOrEmpty(stepUpToken))
        {
            request.Headers.TryAddWithoutValidation(StepUpHeader, stepUpToken);
        }

        if (!string.IsNullOrEmpty(ifMatch))
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiUnavailableException("The site API could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ApiUnavailableException("The site API timed out.", ex);
        }

        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            response.Dispose();
            throw new ApiUnavailableException($"The site API is unavailable ({(int)response.StatusCode}).");
        }

        return response;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> info, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(bytes, info)
                ?? throw new ApiException(response.StatusCode, "empty_response", "The API returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new ApiException(response.StatusCode, "invalid_response", "The API returned a response the POS could not read.", ex.Message);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ProblemDetailsDto? problem = null;
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                problem = JsonSerializer.Deserialize(bytes, Ctx.ProblemDetailsDto);
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body: fall through to a generic problem.
        }

        var errors = problem?.Errors is null
            ? null
            : (IReadOnlyDictionary<string, string[]>)new Dictionary<string, string[]>(problem.Errors);

        throw new ApiException(
            response.StatusCode,
            problem?.Code ?? $"http_{(int)response.StatusCode}",
            problem?.Title ?? response.ReasonPhrase ?? "Request failed",
            problem?.Detail,
            errors,
            problem?.ApprovalId);
    }
}
