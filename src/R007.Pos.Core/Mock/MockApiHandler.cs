using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Mock;

public sealed class MockOptions
{
    /// <summary>Access token lifetime the mock issues (real API: about 15 minutes).</summary>
    public int AccessTokenSeconds { get; set; } = 900;

    /// <summary>VAT rate the mock server applies (ADR-0011: admin-settable, default off).</summary>
    public decimal VatRatePercent { get; set; }

    /// <summary>Simulated latency per request in the demo app (0 in tests).</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
}

/// <summary>A request seen by the mock (for assertions in tests).</summary>
public sealed record RecordedRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// An in-memory stand-in for the 007 Resort &amp; Spa API, implemented as an <see cref="HttpMessageHandler"/> so the
/// <em>entire</em> real client stack (typed client, retries, auth refresh, offline replay) runs against it in demo mode
/// (<c>R007_MOCK=true</c>) and in tests. It follows <c>api/openapi/v1.yaml</c>: bearer + <c>X-Device-Token</c>,
/// <c>Idempotency-Key</c> required on mutations (replays return the original response), <c>If-Match</c> on aggregates
/// with a <c>rowVersion</c>, RFC 7807 problems with stable codes, 202 approval flow, step-up tokens, client UUIDv7 ids.
/// It is deliberately simple server logic (it prices lines, applies VAT if enabled, keeps balances); the real business
/// rules live in the Laravel API, never in the POS.
/// </summary>
public sealed partial class MockApiHandler : HttpMessageHandler
{
    private static PosJsonContext Ctx => PosJsonContext.Default;

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly MockOptions _options;

    private readonly Dictionary<string, Device> _deviceTokens = [];
    private readonly Dictionary<string, (MStaff Staff, DateTimeOffset Expires)> _access = [];
    private readonly Dictionary<string, MStaff> _refresh = [];
    private readonly Dictionary<string, (MStaff Approver, string Permission)> _stepUps = [];
    private readonly List<MStaff> _staff;
    private readonly Dictionary<Guid, FacilityCapabilities> _facilityCaps = [];
    private readonly Dictionary<string, MIdempotent> _idempotency = [];

    private readonly Dictionary<Guid, MOrder> _orders = [];
    private readonly Dictionary<Guid, MTab> _tabs = [];
    private readonly Dictionary<Guid, DiningTable> _tables = [];
    private readonly Dictionary<Guid, MApproval> _approvals = [];
    private readonly Dictionary<Guid, MPayment> _payments = [];
    private readonly Dictionary<Guid, List<Receipt>> _receiptsByOrder = [];
    private readonly Dictionary<Guid, Receipt> _receipts = [];
    private readonly Dictionary<Guid, MCashSession> _cashSessions = [];
    private readonly Dictionary<Guid, MBooking> _bookings = [];
    private readonly Dictionary<Guid, Entitlement> _entitlements = [];
    private readonly List<BookableResource> _resources;
    private readonly List<Membership> _memberships;
    private readonly List<RecordedRequest> _requests = [];
    private int _orderSeq = 100;
    private int _receiptSeq = 5000;
    private int _bookingSeq = 700;

    public MockApiHandler(TimeProvider? time = null, MockOptions? options = null)
    {
        _time = time ?? TimeProvider.System;
        _options = options ?? new MockOptions();

        _staff =
        [
            new MStaff { Id = Guid.Parse("00000000-0000-7000-8000-00000000a001"), Display = "Amaka Cashier", Number = "S-1001", Username = "cashier", Pin = MockData.CashierPin, Nfc = MockData.CashierNfc, Permissions = MockData.CashierPermissions },
            new MStaff { Id = Guid.Parse("00000000-0000-7000-8000-00000000a002"), Display = "Tunde Waiter", Number = "S-1002", Username = "waiter", Pin = MockData.WaiterPin, Nfc = "04AABBCCDD", Permissions = MockData.WaiterPermissions },
            new MStaff { Id = Guid.Parse("00000000-0000-7000-8000-00000000a003"), Display = "Ngozi Supervisor", Number = "S-1003", Username = "supervisor", Pin = MockData.SupervisorPin, Nfc = MockData.SupervisorNfc, Permissions = MockData.SupervisorPermissions },
        ];

        foreach (var caps in new[] { MockData.Restaurant, MockData.IndoorClub, MockData.Reception })
        {
            _facilityCaps[caps.FacilityId] = caps;
        }

        foreach (var facility in new[] { MockData.RestaurantFacilityId, MockData.ClubFacilityId })
        {
            for (var i = 1; i <= 8; i++)
            {
                var id = Guid.CreateVersion7();
                _tables[id] = new DiningTable(id, facility, $"T{i}", 4, "FREE", [], null, 1);
            }
        }

        var sports = Guid.Parse("00000000-0000-7000-8000-000000000104");
        _resources =
        [
            new BookableResource(MockData.TennisCourt, sports, "Tennis Court 1", "TIME_SLOT", 1, 60, null, 5000m, true),
            new BookableResource(MockData.SwimmingPool, sports, "Swimming Pool", "INDIVIDUAL_CAPACITY", 50, 60, MockData.PoolTicket, 3000m, true),
        ];

        _memberships =
        [
            new Membership(Guid.Parse("00000000-0000-7000-8000-00000000b001"), "M-0001", Guid.CreateVersion7(), "Gold", "Chidi Okeke", "ACTIVE", _time.GetUtcNow().AddMonths(-1), _time.GetUtcNow().AddMonths(11)),
            new Membership(Guid.Parse("00000000-0000-7000-8000-00000000b002"), "M-0002", Guid.CreateVersion7(), "Silver", "Bola Adeyemi", "ACTIVE", _time.GetUtcNow().AddMonths(-2), _time.GetUtcNow().AddMonths(10)),
        ];
    }

    /// <summary>Simulate a network outage: every call throws <see cref="HttpRequestException"/>.</summary>
    public bool Offline { get; set; }

    /// <summary>Respond 503 to this many upcoming requests (to exercise retries).</summary>
    public int FailNextWith503 { get; set; }

    /// <summary>
    /// Apply this many upcoming requests on the server but drop the response (connection reset after commit): the
    /// classic case where only an <c>Idempotency-Key</c> replay makes a retry safe.
    /// </summary>
    public int LoseNextResponses { get; set; }

    /// <summary>Minimum client version the server advertises for <c>POS_TERMINAL</c> in <c>GET /system/info</c>.</summary>
    public string MinPosVersion { get; set; } = "0.1.0";

    public IReadOnlyList<Guid> PendingApprovalIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _approvals.Values.Where(a => a.Status == ApprovalStatuses.Pending).Select(a => a.Id)];
            }
        }
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Forget every access token (as if they had all expired), so the next call gets 401 <c>token_expired</c>.</summary>
    public void ExpireAccessTokens()
    {
        lock (_gate)
        {
            _access.Clear();
        }
    }

    /// <summary>Supervisor-side test helper: decide an approval without HTTP.</summary>
    public void DecideApprovalAsSupervisor(Guid approvalId, bool approve, string? note = null)
    {
        lock (_gate)
        {
            var approval = _approvals[approvalId];
            var supervisor = _staff.First(s => s.Username == "supervisor");
            Decide(approval, approve ? ApprovalDecisions.Approve : ApprovalDecisions.Reject, supervisor, note);
        }
    }

    /// <summary>Test helper: confirm a Paystack payment as if the provider webhook arrived.</summary>
    public void ConfirmProviderPayment(string reference)
    {
        lock (_gate)
        {
            var payment = _payments.Values.First(p => p.ProviderReference == reference);
            if (payment.Status == PaymentStatuses.Captured)
            {
                return;
            }

            payment.Status = PaymentStatuses.Captured;
            foreach (var alloc in payment.Allocations)
            {
                var order = _orders[alloc.OrderId];
                order.AmountPaid += alloc.Amount;
                SettleIfPaid(order);
            }
        }
    }

    /// <summary>Test helper: the server's authoritative view of an order.</summary>
    public Order? PeekOrder(Guid id)
    {
        lock (_gate)
        {
            return _orders.TryGetValue(id, out var o) ? ToDto(o) : null;
        }
    }

    public int OrderCount
    {
        get
        {
            lock (_gate)
            {
                return _orders.Count;
            }
        }
    }

    public int PaymentCount
    {
        get
        {
            lock (_gate)
            {
                return _payments.Count;
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_options.Latency > TimeSpan.Zero)
        {
            await Task.Delay(_options.Latency, cancellationToken).ConfigureAwait(false);
        }

        if (Offline)
        {
            throw new HttpRequestException("Mock server offline.");
        }

        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response;
        lock (_gate)
        {
            if (FailNextWith503 > 0)
            {
                FailNextWith503--;
                return Raw(503, Encoding.UTF8.GetBytes("{}"));
            }

            try
            {
                response = Dispatch(request, body);
            }
            catch (MockProblem p)
            {
                response = ProblemResponse(p);
            }
            catch (JsonException ex)
            {
                response = ProblemResponse(new MockProblem(422, "validation_failed", "Invalid request body", ex.Message));
            }

            if (LoseNextResponses > 0)
            {
                LoseNextResponses--;
                response.Dispose();
                throw new HttpRequestException("Connection reset after the server applied the request (mock).");
            }
        }

        return response;
    }

    // Infrastructure --------------------------------------------------------------------------------------------
    private sealed class MockProblem(int status, string code, string title, string? detail = null, Guid? approvalId = null) : Exception(title)
    {
        public int Status { get; } = status;

        public string Code { get; } = code;

        public string Title { get; } = title;

        public string? Detail { get; } = detail;

        public Guid? ApprovalId { get; } = approvalId;
    }

    private sealed record Caller(MStaff Staff);

    private static HttpResponseMessage Raw(int status, byte[] body, string contentType = "application/json")
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    private static HttpResponseMessage ProblemResponse(MockProblem p)
    {
        var dto = new ProblemDetailsDto($"https://api.007resort.com/problems/{p.Code}", p.Title, p.Status, p.Detail, p.Code, null, p.ApprovalId);
        return Raw(p.Status, JsonSerializer.SerializeToUtf8Bytes(dto, Ctx.ProblemDetailsDto), "application/problem+json");
    }

    private static HttpResponseMessage Json<T>(int status, T value, JsonTypeInfo<T> info) =>
        Raw(status, JsonSerializer.SerializeToUtf8Bytes(value, info));

    private static T Read<T>(byte[] body, JsonTypeInfo<T> info) =>
        body.Length == 0
            ? throw new MockProblem(422, "validation_failed", "Request body required")
            : JsonSerializer.Deserialize(body, info) ?? throw new MockProblem(422, "validation_failed", "Request body required");

    private static bool Match(string[] segments, string pattern, out string[] args)
    {
        var parts = pattern.Split('/');
        var captured = new List<string>();
        args = [];
        if (parts.Length != segments.Length)
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "{}")
            {
                captured.Add(segments[i]);
            }
            else if (!string.Equals(parts[i], segments[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        args = [.. captured];
        return true;
    }

    private static Guid G(string value) =>
        Guid.TryParse(value, out var g) ? g : throw new MockProblem(404, "not_found", "Not found");

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            result[key] = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return result;
    }

    private static string Header(HttpRequestMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;

    private static void RequireV7(Guid? id)
    {
        if (id is { } value && value.Version != 7)
        {
            throw new MockProblem(422, "validation_failed", "Client-supplied ids must be UUIDv7");
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private HttpResponseMessage Dispatch(HttpRequestMessage request, byte[] body)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/');
        var query = ParseQuery(request.RequestUri.Query);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        _requests.Add(new RecordedRequest(request.Method.Method, "/" + path, request.RequestUri.Query, headers, Encoding.UTF8.GetString(body)));

        if (!path.StartsWith("api/v1/", StringComparison.Ordinal))
        {
            return path == "health/live" ? Json(200, new Health("ok"), Ctx.Health) : Raw(404, []);
        }

        var seg = path["api/v1/".Length..].Split('/');
        var method = request.Method.Method;

        // Public endpoints
        if (method == "GET" && Match(seg, "system/info", out _))
        {
            return Json(200, new SystemInfo("007resort-api", "1.0.0", "local", null, Now, "Africa/Lagos", "NGN", new Dictionary<string, string> { ["POS_TERMINAL"] = MinPosVersion }, _options.VatRatePercent > 0), Ctx.SystemInfo);
        }

        if (method == "GET" && Match(seg, "health/live", out _))
        {
            return Json(200, new Health("ok"), Ctx.Health);
        }

        if (method == "POST" && Match(seg, "devices/register", out _))
        {
            return WithIdempotency(request, body, () => RegisterDevice(Read(body, Ctx.DeviceRegisterRequest)));
        }

        if (method == "POST" && Match(seg, "auth/staff/login", out _))
        {
            return Login(request, Read(body, Ctx.StaffLoginRequest));
        }

        if (method == "POST" && Match(seg, "auth/staff/refresh", out _))
        {
            return Refresh(Read(body, Ctx.RefreshRequest));
        }

        // Everything else needs a registered device and a valid staff session
        RequireDevice(request);
        var caller = Authenticate(request);

        if (method == "POST" && Match(seg, "auth/staff/step-up", out _))
        {
            return StepUp(Read(body, Ctx.StepUpRequest));
        }

        if (method == "POST" && Match(seg, "auth/staff/logout", out _))
        {
            var token = Header(request, "Authorization")["Bearer ".Length..];
            _access.Remove(token);
            return Raw(204, []);
        }

        if (method is "POST" or "PUT" or "PATCH" or "DELETE")
        {
            return WithIdempotency(request, body, () => Route(request, method, seg, query, body, caller));
        }

        return Route(request, method, seg, query, body, caller);
    }

    private HttpResponseMessage WithIdempotency(HttpRequestMessage request, byte[] body, Func<HttpResponseMessage> handler)
    {
        var key = Header(request, "Idempotency-Key");
        if (string.IsNullOrEmpty(key))
        {
            throw new MockProblem(400, "idempotency_key_missing", "Idempotency-Key header is required");
        }

        var fingerprint = request.Method.Method + " " + request.RequestUri!.PathAndQuery + " " + Convert.ToHexString(SHA256.HashData(body));
        if (_idempotency.TryGetValue(key, out var prior))
        {
            if (prior.Fingerprint != fingerprint)
            {
                throw new MockProblem(422, "idempotency_key_reused", "Idempotency key was used with a different request");
            }

            var replay = Raw(prior.Status, prior.Body, prior.Status is >= 400 ? "application/problem+json" : "application/json");
            replay.Headers.TryAddWithoutValidation("Idempotent-Replayed", "true");
            return replay;
        }

        var response = handler();
        if ((int)response.StatusCode is >= 200 and < 300)
        {
            var bytes = response.Content is null ? [] : response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            _idempotency[key] = new MIdempotent { Fingerprint = fingerprint, Status = (int)response.StatusCode, Body = bytes };
        }

        return response;
    }

    private void RequireDevice(HttpRequestMessage request)
    {
        var token = Header(request, "X-Device-Token");
        if (string.IsNullOrEmpty(token) || !_deviceTokens.ContainsKey(token))
        {
            throw new MockProblem(403, "device_not_registered", "This device is not registered");
        }
    }

    private Caller Authenticate(HttpRequestMessage request)
    {
        var header = Header(request, "Authorization");
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            throw new MockProblem(401, "unauthenticated", "Sign in required");
        }

        var token = header["Bearer ".Length..];
        if (!_access.TryGetValue(token, out var entry))
        {
            throw new MockProblem(401, "token_expired", "The access token is missing or expired");
        }

        if (entry.Expires <= Now)
        {
            _access.Remove(token);
            throw new MockProblem(401, "token_expired", "The access token expired");
        }

        return new Caller(entry.Staff);
    }

    private static void Need(Caller caller, string permission)
    {
        if (!caller.Staff.Permissions.Contains(permission))
        {
            throw new MockProblem(403, "permission_denied", "You do not have permission to do that", permission);
        }
    }

    private HttpResponseMessage RegisterDevice(DeviceRegisterRequest req)
    {
        if (string.Equals(req.RegistrationCode.Trim(), "BAD", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(req.RegistrationCode))
        {
            throw new MockProblem(422, "validation_failed", "Invalid or expired registration code");
        }

        var facility = MockData.FacilityForEnrollmentCode(req.RegistrationCode);
        var device = new Device(Guid.CreateVersion7(), req.Name, req.Kind, "ACTIVE", facility.FacilityId, facility.FacilityId);
        var token = "dev-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _deviceTokens[token] = device;
        return Json(201, new DeviceRegisterResult(device, token), Ctx.DeviceRegisterResult);
    }

    private HttpResponseMessage Login(HttpRequestMessage request, StaffLoginRequest req)
    {
        var deviceToken = Header(request, "X-Device-Token");
        var deviceKnown = _deviceTokens.ContainsKey(deviceToken);
        if (req.CredentialType is CredentialTypes.Pin or CredentialTypes.NfcCard && !deviceKnown)
        {
            throw new MockProblem(403, "device_not_registered", "PIN and NFC login only work from a registered device");
        }

        var staff = req.CredentialType switch
        {
            CredentialTypes.Pin => _staff.FirstOrDefault(s => string.Equals(s.Number, req.Identifier, StringComparison.OrdinalIgnoreCase) && s.Pin == req.Secret),
            CredentialTypes.NfcCard => _staff.FirstOrDefault(s => string.Equals(s.Nfc, req.Secret, StringComparison.OrdinalIgnoreCase)),
            CredentialTypes.Password => _staff.FirstOrDefault(s => string.Equals(s.Username, req.Identifier, StringComparison.OrdinalIgnoreCase) && s.Pin == req.Secret),
            _ => null,
        };

        if (staff is null)
        {
            throw new MockProblem(401, "invalid_credentials", "Invalid credentials");
        }

        return Json(200, IssueTokens(staff), Ctx.AuthResult);
    }

    private AuthResult IssueTokens(MStaff staff)
    {
        var access = "at-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var refresh = "rt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _access[access] = (staff, Now.AddSeconds(_options.AccessTokenSeconds));
        _refresh[refresh] = staff;
        return new AuthResult(access, refresh, _options.AccessTokenSeconds, staff.ToDto(), new SessionRef(Guid.CreateVersion7(), Now.AddHours(12), null));
    }

    private HttpResponseMessage Refresh(RefreshRequest req)
    {
        // Refresh tokens are single-use and rotate (contract): a replayed token is rejected.
        if (!_refresh.Remove(req.RefreshToken, out var staff))
        {
            throw new MockProblem(401, "unauthenticated", "Refresh token is invalid or was already used");
        }

        return Json(200, IssueTokens(staff), Ctx.AuthResult);
    }

    private HttpResponseMessage StepUp(StepUpRequest req)
    {
        var approver = req.CredentialType switch
        {
            CredentialTypes.Pin => _staff.FirstOrDefault(s => string.Equals(s.Number, req.Identifier, StringComparison.OrdinalIgnoreCase) && s.Pin == req.Secret),
            CredentialTypes.NfcCard => _staff.FirstOrDefault(s => string.Equals(s.Nfc, req.Secret, StringComparison.OrdinalIgnoreCase)),
            _ => _staff.FirstOrDefault(s => string.Equals(s.Username, req.Identifier, StringComparison.OrdinalIgnoreCase) && s.Pin == req.Secret),
        };

        if (approver is null)
        {
            throw new MockProblem(401, "invalid_credentials", "Invalid credentials");
        }

        if (!approver.Permissions.Contains(req.Permission))
        {
            throw new MockProblem(403, "permission_denied", "That staff member cannot authorise this action", req.Permission);
        }

        var token = "su-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        _stepUps[token] = (approver, req.Permission);
        return Json(200, new StepUpResult(token, 120, new StepUpApprover(approver.Id, approver.Display)), Ctx.StepUpResult);
    }

    /// <summary>True when the caller holds the approve permission itself or presents a valid single-use step-up token for it.</summary>
    private bool IsPreApproved(HttpRequestMessage request, Caller caller, string approvePermission)
    {
        if (caller.Staff.Permissions.Contains(approvePermission))
        {
            return true;
        }

        var token = Header(request, "X-Step-Up-Token");
        if (!string.IsNullOrEmpty(token) && _stepUps.TryGetValue(token, out var entry) && entry.Permission == approvePermission)
        {
            _stepUps.Remove(token); // single use
            return true;
        }

        if (!string.IsNullOrEmpty(token))
        {
            throw new MockProblem(403, "step_up_required", "The step-up token is invalid, expired or for another action");
        }

        return false;
    }

    private HttpResponseMessage Route(HttpRequestMessage request, string method, string[] seg, Dictionary<string, string> query, byte[] body, Caller caller)
    {
        string[] a;

        // Facility & catalog
        if (method == "GET" && Match(seg, "facilities/{}/capabilities", out a))
        {
            return _facilityCaps.TryGetValue(G(a[0]), out var caps)
                ? Json(200, caps, Ctx.FacilityCapabilities)
                : throw new MockProblem(404, "not_found", "Facility not found");
        }

        if (method == "GET" && Match(seg, "organization/facilities/{}", out a))
        {
            var id = G(a[0]);
            return Json(200, new Facility(id, "MOCK", MockData.FacilityName(id), "FACILITY", "ACTIVE"), Ctx.Facility);
        }

        if (method == "GET" && Match(seg, "catalog/categories", out _))
        {
            return Json(200, new Page<Category>(MockData.Categories, null), Ctx.PageCategory);
        }

        if (method == "GET" && Match(seg, "catalog/products", out _))
        {
            return Json(200, new Page<Product>(SearchProducts(query.GetValueOrDefault("q")), null), Ctx.PageProduct);
        }

        var response = RouteTablesOrdersTabs(request, method, seg, query, body, caller)
            ?? RoutePayments(request, method, seg, query, body, caller)
            ?? RouteReception(request, method, seg, query, body, caller);

        return response ?? throw new MockProblem(404, "not_found", $"No route for {method} /{string.Join('/', seg)}");
    }

    private static List<Product> SearchProducts(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return [.. MockData.Products];
        }

        if (MockData.Barcodes.TryGetValue(q.Trim(), out var byBarcode))
        {
            return [.. MockData.Products.Where(p => p.Id == byBarcode)];
        }

        return
        [
            .. MockData.Products.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (p.Sku?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)),
        ];
    }
}
