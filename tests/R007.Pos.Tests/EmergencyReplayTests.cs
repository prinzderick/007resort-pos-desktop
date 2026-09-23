using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;

namespace R007.Pos.Tests;

public sealed class EmergencyReplayTests
{
    private static readonly OfflinePolicy CashOk = new(true, OfflinePaymentPolicy.CashOnly);

    [Theory]
    [InlineData("POST", "api/v1/orders", true)]
    [InlineData("POST", "api/v1/tabs", true)]
    [InlineData("POST", "api/v1/orders/0192f6a0-0000-7000-8000-000000000601/lines", true)]
    [InlineData("POST", "api/v1/orders/0192f6a0-0000-7000-8000-000000000601/send", true)]
    [InlineData("POST", "api/v1/orders/0192f6a0-0000-7000-8000-000000000601/void", false)]
    [InlineData("POST", "api/v1/orders/0192f6a0-0000-7000-8000-000000000601/lines/x/adjustments", false)]
    [InlineData("POST", "api/v1/approvals/x/decision", false)]
    [InlineData("POST", "api/v1/payments/x/refund", false)]
    [InlineData("POST", "api/v1/payments/paystack/initialize", false)]
    [InlineData("POST", "api/v1/bookings/hold", false)]
    [InlineData("POST", "api/v1/cash-sessions/x/close", false)]
    [InlineData("POST", "api/v1/entitlement-tokens/x/redeem", false)]
    [InlineData("POST", "api/v1/auth/staff/login", false)]
    [InlineData("DELETE", "api/v1/orders/x/lines/y", false)]
    public void Allowlist_OnlyPermitsSafeOperations(string method, string path, bool expected)
    {
        using var pos = new TestPos();
        pos.Emergency.Policy = CashOk;

        Assert.Equal(expected, pos.Emergency.IsQueueable(method, path, "{}"));
    }

    [Fact]
    public void FacilityPolicy_GatesEverything()
    {
        using var pos = new TestPos();
        pos.Emergency.Policy = OfflinePolicy.Disabled;

        Assert.False(pos.Emergency.IsQueueable("POST", "api/v1/orders", "{}"));
        Assert.False(pos.Emergency.IsQueueable("POST", "api/v1/payments", PaymentBody(TenderTypes.Cash)));
    }

    [Theory]
    [InlineData(TenderTypes.Cash, true)]
    [InlineData(TenderTypes.Card, false)]
    [InlineData(TenderTypes.PosTerminal, false)]
    [InlineData(TenderTypes.Transfer, false)]
    public void OnlyCashPayments_CanBeQueued(string tender, bool expected)
    {
        using var pos = new TestPos();
        pos.Emergency.Policy = new OfflinePolicy(true, OfflinePaymentPolicy.All); // even when the facility says ALL

        Assert.Equal(expected, pos.Emergency.IsQueueable("POST", "api/v1/payments", PaymentBody(tender)));
    }

    [Fact]
    public void MixedCashAndCardPayment_IsNotQueueable()
    {
        using var pos = new TestPos();
        pos.Emergency.Policy = CashOk;
        var request = new CreatePaymentRequest(
            Guid.NewGuid(),
            [new AllocationInput(Guid.NewGuid(), 3000m)],
            [new TenderInput(TenderTypes.Cash, 1000m), new TenderInput(TenderTypes.Card, 2000m, "RRN1")]);

        Assert.False(pos.Emergency.IsQueueable("POST", "api/v1/payments", JsonSerializer.Serialize(request, PosJsonContext.Default.CreatePaymentRequest)));
    }

    private static string PaymentBody(string tender) =>
        JsonSerializer.Serialize(
            new CreatePaymentRequest(Guid.NewGuid(), [new AllocationInput(Guid.NewGuid(), 1000m)], [new TenderInput(tender, 1000m, tender == TenderTypes.Cash ? null : "REF")]),
            PosJsonContext.Default.CreatePaymentRequest);

    [Fact]
    public async Task Outage_FullOrderLifecycle_IsQueuedThenReplayedInOrder_WithClientIds()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        await OpenSession(pos);
        var order = await CreateAndSendOfflineAsync(pos);

        Assert.Equal(0, pos.Server.OrderCount);
        Assert.Equal(4, await pos.Queue.CountPendingAsync()); // create, add line, send, cash tender

        pos.Server.Offline = false;
        var result = await pos.Replay.DrainAsync();

        Assert.Equal(new ReplayResult(4, 0, 0, ReplayStop.None), result);
        var server = pos.Server.PeekOrder(order.Id)!;
        Assert.Equal(order.Id, server.Id);                         // client-generated UUIDv7 became the server id
        Assert.Equal(OrderStatuses.Settled, server.Status);        // sent, then paid by the queued cash tender
        Assert.Equal(1, pos.Server.PaymentCount);
        Assert.Equal(5000m, server.AmountPaid);
        Assert.Equal(0, await pos.Queue.CountPendingAsync());
    }

    [Fact]
    public async Task Replay_UsesOriginalIdempotencyKeys_AndSetsIfMatchFromTrackedVersions()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        await OpenSession(pos);
        await CreateAndSendOfflineAsync(pos);
        var keys = (await pos.Queue.GetPendingAsync()).Select(o => o.IdempotencyKey.ToString("D")).ToList();
        pos.Server.Offline = false;

        await pos.Replay.DrainAsync();

        var replayed = pos.Server.Requests.Where(r => r.Method == "POST" && r.Headers.ContainsKey("X-Offline-Captured-At")).ToList();
        Assert.Equal(keys, replayed.Select(r => r.Headers["Idempotency-Key"]));
        Assert.All(replayed, r => Assert.True(r.Headers.ContainsKey("X-Offline-Staff-Id")));
        Assert.Contains(replayed, r => r.Path.EndsWith("/lines", StringComparison.Ordinal) && r.Headers["If-Match"] == "\"v1\"");
        Assert.Contains(replayed, r => r.Path.EndsWith("/send", StringComparison.Ordinal) && r.Headers["If-Match"] == "\"v2\"");
    }

    [Fact]
    public async Task CrashAfterServerApplied_ButBeforeMarkedReplayed_DoesNotDuplicate()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        await OpenSession(pos);
        var order = await CreateAndSendOfflineAsync(pos);
        pos.Server.Offline = false;
        await pos.Replay.DrainAsync();
        Assert.Equal(1, pos.Server.PaymentCount);

        // Simulate a crash: the queue never learned the payment succeeded, so on restart it replays the same entry again.
        var again = new QueuedOperation(
            Guid.Parse(pos.Server.Requests.Last(r => r.Path == "/api/v1/payments" && r.Method == "POST").Headers["Idempotency-Key"]),
            "POST",
            "api/v1/payments",
            pos.Server.Requests.Last(r => r.Path == "/api/v1/payments" && r.Method == "POST").Body,
            pos.Env.Time.GetUtcNow());
        await pos.Queue.EnqueueAsync(again);

        var result = await pos.Replay.DrainAsync();

        Assert.Equal(1, result.Replayed);
        Assert.Equal(1, pos.Server.PaymentCount); // server replayed the original result; no second payment
        Assert.Equal(5000m, pos.Server.PeekOrder(order.Id)!.AmountPaid);
    }

    [Fact]
    public async Task TransientFailure_StopsTheDrain_PreservingOrder()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        await OpenSession(pos);
        await CreateAndSendOfflineAsync(pos);
        pos.Server.Offline = false;
        pos.Server.FailNextWith503 = 10; // server up but unhealthy

        var result = await pos.Replay.DrainAsync();

        Assert.Equal(ReplayStop.ServerUnavailable, result.Stopped);
        Assert.Equal(4, result.Remaining);
        Assert.Equal(0, pos.Server.OrderCount); // nothing applied out of order
    }

    [Fact]
    public async Task ServerRefusal_IsRecordedAsRejected_ShownToStaff_AndTheDrainContinues()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        await OpenSession(pos);
        // Create + pay the order online, then queue a *second* cash payment for it while "offline" (balance already 0).
        var product = pos.Product(MockData.Beer);
        var online = await pos.Ctx.Orders.AddProductAsync(null, product, 1, null, new(pos.Ctx.FacilityId));
        await pos.Env.Api.CreatePaymentAsync(
            new CreatePaymentRequest(pos.Ctx.FacilityId, [new AllocationInput(online.Id, online.AmountDue)], [new TenderInput(TenderTypes.Cash, online.AmountDue)], pos.Ctx.CashSession!.Id),
            IdempotencyKeys.New());
        pos.Emergency.Policy = CashOk;
        var duplicate = new CreatePaymentRequest(pos.Ctx.FacilityId, [new AllocationInput(online.Id, 1500m)], [new TenderInput(TenderTypes.Cash, 1500m, null, null, ClientIds.New())], pos.Ctx.CashSession.Id);
        await pos.Emergency.QueueAsync("api/v1/payments", JsonSerializer.Serialize(duplicate, PosJsonContext.Default.CreatePaymentRequest), IdempotencyKeys.New(), "Cash 1500 for the same order", $"api/v1/orders/{online.Id:D}", false);
        var second = await pos.Ctx.Orders.AddProductAsync(null, product, 1, null, new(pos.Ctx.FacilityId));
        var followUp = new CreatePaymentRequest(pos.Ctx.FacilityId, [new AllocationInput(second.Id, 1500m)], [new TenderInput(TenderTypes.Cash, 1500m, null, null, ClientIds.New())], pos.Ctx.CashSession.Id);
        await pos.Emergency.QueueAsync("api/v1/payments", JsonSerializer.Serialize(followUp, PosJsonContext.Default.CreatePaymentRequest), IdempotencyKeys.New(), "Cash 1500 for order 2", $"api/v1/orders/{second.Id:D}", false);

        var result = await pos.Replay.DrainAsync();

        Assert.Equal(1, result.Replayed);
        Assert.Equal(1, result.Rejected);
        var rejected = Assert.Single(await pos.Queue.GetRejectedAsync());
        Assert.Equal("Cash 1500 for the same order", rejected.Operation.Description);
        Assert.Contains("order_state_invalid", rejected.Reason, StringComparison.Ordinal);
        Assert.Equal(OrderStatuses.Settled, pos.Server.PeekOrder(second.Id)!.Status);
    }

    [Fact]
    public async Task NotSignedIn_ReplayWaits_NothingIsSent()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        await OpenSession(pos);
        await CreateAndSendOfflineAsync(pos);
        pos.Server.Offline = false;
        pos.Env.Auth.SignOut();

        var result = await pos.Replay.DrainAsync();

        Assert.Equal(ReplayStop.NotSignedIn, result.Stopped);
        Assert.Equal(4, result.Remaining);
    }

    [Fact]
    public async Task CardPayment_DuringOutage_IsRefused_AndNeverQueued()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var online = await pos.Ctx.Orders.AddProductAsync(null, pos.Product(MockData.Beer), 1, null, new(pos.Ctx.FacilityId));
        pos.Server.Offline = true;
        pos.Emergency.Policy = new OfflinePolicy(true, OfflinePaymentPolicy.All);

        var payment = new PaymentViewModelHarness(pos, online);
        await payment.PayWithAsync(TenderTypes.Card, "RRN-1");

        Assert.Equal(0, await pos.Queue.CountPendingAsync());
        Assert.False(payment.Modal.Paid);
        Assert.False(payment.Modal.QueuedPending);
        Assert.Contains("cash", payment.Modal.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task OpenSession(TestPos pos)
    {
        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
    }

    /// <summary>Goes offline mid-sale and takes the whole cash order through the real workflow (create, add, send, pay).</summary>
    private static async Task<R007.Pos.ViewModels.Services.WorkingOrder> CreateAndSendOfflineAsync(TestPos pos)
    {
        pos.Server.Offline = true;
        pos.Emergency.Policy = CashOk;
        var target = new R007.Pos.ViewModels.Services.OrderTarget(pos.Ctx.FacilityId);
        var order = await pos.Ctx.Orders.AddProductAsync(null, pos.Product(MockData.Beer), 1, null, target);
        order = await pos.Ctx.Orders.AddProductAsync(order, pos.Product(MockData.Jollof), 1, "Extra spicy", target);
        Assert.True(order.IsPendingConfirmation);
        Assert.Null(order.Total); // no server total offline
        Assert.Equal(5000m, order.EstimatedTotal);
        order = await pos.Ctx.Orders.SendAsync(order);
        // Pay: the API prices Beer 1500 + Jollof 3500 = 5000
        var payment = new PaymentViewModelHarness(pos, order);
        await payment.PayWithAsync(TenderTypes.Cash, null);
        Assert.True(payment.Modal.QueuedPending);
        return order;
    }
}
