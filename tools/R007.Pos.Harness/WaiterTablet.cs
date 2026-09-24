using System.Net;
using System.Text.Json.Nodes;

namespace R007.Pos.Harness;

/// <summary>
/// The waiter's tablet as a "curl-equivalent" client: a freshly registered MOBILE_TABLET (mode ATTENDANT) checked out to <c>wait1</c> at a
/// facility. The POS under test never collects at a table, so this plays the waiter: create / send / serve / bill an order, collect money
/// (card machine, cash, transfer), declare a cash handover. A NEW tablet per run keeps it away from the seeded ones other agents use.
/// </summary>
public sealed class WaiterTablet
{
    private WaiterTablet(NodeContext node, string deviceToken, Guid deviceId, string bearer, Guid staffId, Guid facilityId)
    {
        Node = node;
        DeviceToken = deviceToken;
        DeviceId = deviceId;
        Bearer = bearer;
        StaffId = staffId;
        FacilityId = facilityId;
    }

    public NodeContext Node { get; }

    public string DeviceToken { get; }

    public Guid DeviceId { get; }

    public string Bearer { get; }

    public Guid StaffId { get; }

    public Guid FacilityId { get; }

    private RawApi Raw => Node.Raw;

    /// <summary>Registers a tablet, signs <c>wait1</c> in on it and (when <paramref name="checkOut"/>) checks it out to wait1 at the facility.</summary>
    public static async Task<WaiterTablet> StartAsync(NodeContext node, string facilityCode, bool checkOut = true)
    {
        var code = await node.NewRegistrationCodeAsync(facilityCode).ConfigureAwait(false);
        var registered = await Throttle.RetryAsync(() => node.Raw.PostAsync("devices/register", new JsonObject
        {
            ["name"] = $"Harness waiter tablet {DateTime.UtcNow:HHmmss}",
            ["kind"] = "MOBILE_TABLET",
            ["mode"] = "ATTENDANT",
            ["hardwareId"] = "harness-tablet-" + Guid.NewGuid().ToString("N")[..8],
            ["platform"] = "android",
            ["appVersion"] = "1.0.0",
            ["registrationCode"] = code,
        })).ConfigureAwait(false);
        registered.Expect(HttpStatusCode.Created, "register waiter tablet");
        var deviceToken = registered.Json["deviceToken"]!.GetValue<string>();
        var deviceId = Guid.Parse(registered.Json["device"]!["id"]!.GetValue<string>());
        var facility = await node.FacilityIdAsync(facilityCode).ConfigureAwait(false);
        var login = (await Throttle.RetryAsync(() => node.Raw.PostAsync("auth/staff/login", new JsonObject { ["credentialType"] = "PIN", ["identifier"] = "S-0001", ["secret"] = "1234" }, deviceToken: deviceToken)).ConfigureAwait(false)).Ok("wait1 login").Json;
        var bearer = login["accessToken"]!.GetValue<string>();
        var staffId = Guid.Parse(login["staff"]!["id"]!.GetValue<string>());
        if (checkOut)
        {
            var manager = await node.Raw.LoginPinAsync("S-0011").ConfigureAwait(false);
            (await node.Raw.PostAsync($"devices/{deviceId:D}/checkout", new JsonObject { ["staffId"] = staffId.ToString("D"), ["facilityId"] = facility.ToString("D") }, manager).ConfigureAwait(false)).Ok("check the tablet out to wait1");
        }

        return new WaiterTablet(node, deviceToken, deviceId, bearer, staffId, facility);
    }

    public Task<RawResponse> GetAsync(string path) => Raw.GetAsync(path, Bearer, DeviceToken);

    public Task<RawResponse> PostAsync(string path, JsonObject? body = null, string? idempotencyKey = null, string? ifMatch = null) =>
        Raw.PostAsync(path, body ?? new JsonObject(), Bearer, DeviceToken, idempotencyKey, ifMatch);

    /// <summary>A takeaway (or table) order of <paramref name="quantity"/> x <paramref name="productId"/>, sent and served (products with no prep route only).</summary>
    public async Task<JsonNode> ServedOrderAsync(Guid productId, int quantity, Guid? tableId = null)
    {
        var body = new JsonObject
        {
            ["facilityId"] = FacilityId.ToString("D"),
            ["channel"] = tableId is null ? "TAKEAWAY" : "DINE_IN",
            ["lines"] = new JsonArray(new JsonObject { ["productId"] = productId.ToString("D"), ["quantity"] = quantity }),
        };
        if (tableId is not null)
        {
            body["tableId"] = tableId.Value.ToString("D");
        }

        var order = (await PostAsync("orders", body).ConfigureAwait(false)).Expect(HttpStatusCode.Created, "waiter creates the order").Json;
        var id = order["id"]!.GetValue<string>();
        order = (await PostAsync($"orders/{id}/send", null, null, $"\"v{order["rowVersion"]}\"").ConfigureAwait(false)).Ok("waiter sends the order").Json;
        order = (await PostAsync($"orders/{id}/serve", null, null, $"\"v{order["rowVersion"]}\"").ConfigureAwait(false)).Ok("waiter serves the order").Json;
        return order;
    }

    public async Task<JsonNode> PrintBillAsync(string orderId) =>
        (await PostAsync($"orders/{orderId}/bill").ConfigureAwait(false)).Ok("waiter prints the bill").Json;

    public Task<RawResponse> CollectAsync(string orderId, JsonObject body, string? idempotencyKey = null) =>
        PostAsync($"orders/{orderId}/collections", body, idempotencyKey);

    public Task<RawResponse> CollectCardAsync(string orderId, string amount, string approval, string slip, Guid? id = null, string? idempotencyKey = null) =>
        CollectAsync(orderId, new JsonObject
        {
            ["id"] = (id ?? Guid.CreateVersion7()).ToString("D"),
            ["tenderType"] = "CARD_TERMINAL",
            ["amount"] = amount,
            ["approvalCode"] = approval,
            ["slipReference"] = slip,
            ["last4"] = "4242",
        }, idempotencyKey);

    public Task<RawResponse> CollectCashAsync(string orderId, string amount, string tendered) =>
        CollectAsync(orderId, new JsonObject
        {
            ["id"] = Guid.CreateVersion7().ToString("D"),
            ["tenderType"] = "CASH",
            ["amount"] = amount,
            ["tendered"] = tendered,
        });

    public Task<RawResponse> DeclareHandoverAsync(string amount) =>
        PostAsync("cash-handovers", new JsonObject { ["id"] = Guid.CreateVersion7().ToString("D"), ["facilityId"] = FacilityId.ToString("D"), ["declaredAmount"] = amount });

    public static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
}
