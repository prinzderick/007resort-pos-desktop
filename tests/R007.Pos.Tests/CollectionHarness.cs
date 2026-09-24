using System.Net.Http.Headers;
using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Http;
using R007.Pos.Core.Mock;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

/// <summary>
/// The waiter's tablet, simulated over the same mock server: the POS never collects at a table, so tests drive that side with plain HTTP
/// (the equivalent of the waiter app calling <c>POST /orders/{id}/collections</c>).
/// </summary>
public sealed class WaiterSim : IDisposable
{
    private readonly HttpClient _http;

    private WaiterSim(HttpClient http, R007ApiClient api, AuthResult login)
    {
        _http = http;
        Api = api;
        Login = login;
    }

    public R007ApiClient Api { get; }

    public AuthResult Login { get; }

    public Guid StaffId => Login.Staff.Id;

    public static async Task<WaiterSim> StartAsync(TestPos pos, string staffNumber = "S-1002", string pin = MockData.WaiterPin, string code = "RESTAURANT")
    {
        var auth = new AuthState();
        var http = PosHttp.CreateClient(pos.Server, auth, new ConnectivityMonitor(), new ServerEndpoint(new Uri("http://mock.local/")), TimeSpan.FromSeconds(5),
            new RetryOptions { MaxRetries = 0, BaseDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(2) }, pos.Env.Time, (_, _) => Task.CompletedTask);
        var api = new R007ApiClient(http);
        var reg = await api.RegisterDeviceAsync(new DeviceRegisterRequest("Waiter tablet", "MOBILE_TABLET", "hw-waiter-" + Guid.NewGuid().ToString("N")[..6], code), IdempotencyKeys.New());
        auth.SetDeviceToken(reg.DeviceToken);
        var login = await api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, staffNumber, pin));
        auth.SignIn(login, pos.Env.Time.GetUtcNow());
        return new WaiterSim(http, api, login);
    }

    public async Task<Order> BilledOrderAsync(Guid facilityId, Guid productId, int quantity = 1, Guid? tableId = null)
    {
        var order = await Api.CreateOrderAsync(new CreateOrderRequest(facilityId, tableId, null, OrderChannels.DineIn, null, [new OrderLineInput(productId, quantity, null, ClientIds.New())], ClientIds.New()), IdempotencyKeys.New());
        order = await Api.SendOrderAsync(order.Id, order.RowVersion, IdempotencyKeys.New());
        await Api.PrintBillAsync(order.Id, new BillRequest(), IdempotencyKeys.New());
        return await Api.GetOrderAsync(order.Id);
    }

    /// <summary>POST /orders/{id}/collections; throws <see cref="ApiException"/> like the real client would.</summary>
    public async Task<CollectionResult> CollectAsync(Guid orderId, CollectionRequest request, string? key = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"api/v1/orders/{orderId:D}/collections", UriKind.Relative))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, PosJsonContext.Default.CollectionRequest)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? IdempotencyKeys.New());
        using var response = await _http.SendAsync(message);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
        {
            var problem = JsonSerializer.Deserialize(bytes, PosJsonContext.Default.ProblemDetailsDto);
            throw new ApiException(response.StatusCode, problem?.Code ?? "unknown", problem?.Title ?? "failed", problem?.Detail);
        }

        return JsonSerializer.Deserialize(bytes, PosJsonContext.Default.CollectionResult)!;
    }

    public Task<CollectionResult> CollectCardAsync(Guid orderId, decimal amount, string approval, string? slip = null) =>
        CollectAsync(orderId, new CollectionRequest(CollectionTenders.CardTerminal, amount, ClientIds.New(), null, null, approval, slip ?? "SLIP-" + approval, "4242"));

    public async Task<CashHandover> DeclareHandoverAsync(decimal amount, Guid facilityId)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("api/v1/cash-handovers", UriKind.Relative))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new DeclareHandoverRequest(amount, ClientIds.New(), facilityId), PosJsonContext.Default.DeclareHandoverRequest)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.TryAddWithoutValidation("Idempotency-Key", IdempotencyKeys.New());
        using var response = await _http.SendAsync(message);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.IsSuccessStatusCode, System.Text.Encoding.UTF8.GetString(bytes));
        return JsonSerializer.Deserialize(bytes, PosJsonContext.Default.CashHandover)!;
    }

    public void Dispose() => _http.Dispose();
}
