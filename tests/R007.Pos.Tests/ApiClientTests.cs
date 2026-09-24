using System.Net;
using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;

namespace R007.Pos.Tests;

public sealed class ApiClientTests
{
    [Fact]
    public async Task SystemInfo_ParsesContractShape()
    {
        using var env = new TestEnv();
        var info = await env.Api.GetSystemInfoAsync();
        Assert.Equal("007resort-api", info.Service);
        Assert.Equal("NGN", info.Currency);
        Assert.Equal("local", info.DeploymentMode);
    }

    [Fact]
    public async Task Login_ThenAuthenticatedCall_SendsDeviceTokenAndBearer()
    {
        using var env = new TestEnv();
        await env.SignInAsync();

        var caps = await env.Api.GetCapabilitiesAsync(env.FacilityId);

        Assert.True(caps.Has(Capabilities.Pos));
        var call = env.Server.Requests.Last();
        Assert.StartsWith("dev-", call.Headers["X-Device-Token"]);
        Assert.StartsWith("Bearer at-", call.Headers["Authorization"]);
        Assert.True(Guid.TryParse(call.Headers["X-Correlation-Id"], out _));
    }

    [Fact]
    public async Task BadCredentials_ThrowsProblemWithStableCode()
    {
        using var env = new TestEnv();
        var reg = await env.Api.RegisterDeviceAsync(new DeviceRegisterRequest("P", DeviceKinds.PosTerminal, "hw", "X"), IdempotencyKeys.New());
        env.Auth.SetDeviceToken(reg.DeviceToken);

        var ex = await Assert.ThrowsAsync<ApiException>(() => env.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, "S-1001", "0000")));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal("invalid_credentials", ex.Code);
    }

    [Fact]
    public async Task UnregisteredDevice_IsRejected()
    {
        using var env = new TestEnv();
        var ex = await Assert.ThrowsAsync<ApiException>(() => env.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, "S-1001", MockData.CashierPin)));
        Assert.Equal("device_not_registered", ex.Code);
    }

    [Fact]
    public async Task MoneyIsDecimal_RoundTrippedFromStrings()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var order = await env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 3)]), IdempotencyKeys.New());

        Assert.Equal(4500m, order.Total);
        Assert.Equal(4500m, order.BalanceDue);
        var raw = env.Server.Requests.Last();
        Assert.Contains("\"productId\"", raw.Body);
        var fetched = await env.Api.GetOrderAsync(order.Id);
        Assert.Equal(order.Total, fetched.Total);
    }

    [Fact]
    public async Task Mutation_SendsIdempotencyKey_AndReplayReturnsOriginal()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var key = IdempotencyKeys.New();
        var request = new CreateOrderRequest(env.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]);

        var first = await env.Api.CreateOrderAsync(request, key);
        var second = await env.Api.CreateOrderAsync(request, key);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, env.Server.OrderCount);
        Assert.Equal(key, env.Server.Requests.Last().Headers["Idempotency-Key"]);
    }

    [Fact]
    public async Task ReusedKeyWithDifferentBody_IsRejected()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var key = IdempotencyKeys.New();
        await env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId), key);

        var ex = await Assert.ThrowsAsync<ApiException>(() => env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId, Channel: OrderChannels.Takeaway), key));

        Assert.Equal("idempotency_key_reused", ex.Code);
    }

    [Fact]
    public async Task IfMatch_UsesRowVersionEtag_AndStaleVersionIsRejected()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var order = await env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId), IdempotencyKeys.New());

        var updated = await env.Api.AddLineAsync(order.Id, order.RowVersion, new OrderLineInput(MockData.Soda, 2), IdempotencyKeys.New());
        Assert.Equal($"\"v{order.RowVersion}\"", env.Server.Requests.Last().Headers["If-Match"]);
        Assert.Equal(order.RowVersion + 1, updated.RowVersion);

        var ex = await Assert.ThrowsAsync<ApiException>(() => env.Api.AddLineAsync(order.Id, order.RowVersion, new OrderLineInput(MockData.Soda, 1), IdempotencyKeys.New()));
        Assert.Equal("concurrency_conflict", ex.Code);
        Assert.Equal(HttpStatusCode.PreconditionFailed, ex.Status);
    }

    [Fact]
    public async Task Transient503_IsRetried_WithSameIdempotencyKey()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        env.Server.FailNextWith503 = 2;

        var order = await env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId), IdempotencyKeys.New());

        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(1, env.Server.OrderCount);
        var creates = env.Server.Requests.Where(r => r.Method == "POST" && r.Path == "/api/v1/orders").ToList();
        Assert.Single(creates); // 503s short-circuit before reaching the recorded handler; the successful attempt carries the key
        Assert.False(string.IsNullOrEmpty(creates[0].Headers["Idempotency-Key"]));
    }

    [Fact]
    public async Task PersistentOutage_SurfacesAsApiUnavailable_AndMarksOffline()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        env.Server.Offline = true;

        await Assert.ThrowsAsync<ApiUnavailableException>(() => env.Api.GetTablesAsync(env.FacilityId));

        Assert.True(env.Connectivity.IsOffline);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshedTransparently_AndCallSucceeds()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var firstToken = env.Auth.AccessToken;
        env.Server.ExpireAccessTokens();

        var tables = await env.Api.GetTablesAsync(env.FacilityId);

        Assert.NotEmpty(tables);
        Assert.NotEqual(firstToken, env.Auth.AccessToken);
    }

    [Fact]
    public async Task FailedRefresh_ExpiresTheSession()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var expired = false;
        env.Auth.SessionExpired += (_, _) => expired = true;
        env.Server.ExpireAccessTokens();
        // Burn the refresh token so the rotation fails.
        env.Auth.UpdateTokens(new AuthResult("x", "rt-invalid", 60, env.Auth.Staff!, null), env.Time.GetUtcNow());

        await Assert.ThrowsAsync<ApiException>(() => env.Api.GetTablesAsync(env.FacilityId));

        Assert.True(expired);
        Assert.False(env.Auth.IsSignedIn);
    }

    [Fact]
    public async Task PermissionDenied_IsAStableCode()
    {
        using var env = new TestEnv();
        await env.SignInAsync(staffNumber: "S-1002", pin: MockData.WaiterPin); // waiter cannot take payments
        var order = await env.Api.CreateOrderAsync(new CreateOrderRequest(env.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]), IdempotencyKeys.New());

        var ex = await Assert.ThrowsAsync<ApiException>(() => env.Api.CreatePaymentAsync(
            new CreatePaymentRequest(env.FacilityId, [new AllocationInput(order.Id, order.Total)], [new TenderInput(TenderTypes.Cash, order.Total)]),
            IdempotencyKeys.New()));

        Assert.True(ex.IsPermissionDenied);
    }

    [Fact]
    public async Task ListCalls_FollowCursorsAndUnwrapItems()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var products = await env.Api.GetProductsAsync(env.FacilityId);
        var barcode = await env.Api.GetProductsAsync(env.FacilityId, "6001234500011");

        Assert.Equal(MockData.Products.Count, products.Count);
        Assert.Equal(MockData.Beer, Assert.Single(barcode).Id);
    }
}
