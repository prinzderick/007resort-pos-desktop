using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Tests;

/// <summary>
/// Regression tests for deviations found by driving the POS against the REAL node (docs/REAL_API_TEST_REPORT.md). Each test pins one
/// behaviour of the built API that the in-memory mock originally did not have.
/// </summary>
public sealed class RealNodeFidelityTests
{
    private static PosJsonContext Ctx => PosJsonContext.Default;

    [Fact]
    public void ProblemStatus_ThatIsAString_DoesNotTurnABusinessErrorIntoAParseFailure()
    {
        // The node once emitted "status":"DRAFT" on order_state_invalid (a domain value overwrote the RFC 7807 status).
        const string body = """{"type":"urn:r007:problem:order_state_invalid","title":"Conflict","status":"DRAFT","code":"order_state_invalid","detail":"This facility takes payment after service; the order is DRAFT.","paymentTiming":"PAY_AFTER_SERVICE"}""";
        var problem = JsonSerializer.Deserialize(body, Ctx.ProblemDetailsDto)!;

        Assert.Equal("order_state_invalid", problem.Code);
        Assert.Null(problem.Status);
        Assert.Equal(409, JsonSerializer.Deserialize("""{"status":409,"code":"x"}""", Ctx.ProblemDetailsDto)!.Status);
        Assert.Equal(409, JsonSerializer.Deserialize("""{"status":"409","code":"x"}""", Ctx.ProblemDetailsDto)!.Status);
    }

    [Fact]
    public void CashSessionTotals_NonCash_IsAnObjectWhenFilled_AndAnEmptyArrayWhenEmpty()
    {
        // PHP serialises an empty map as [] and a filled one as {..}.
        var empty = JsonSerializer.Deserialize("""{"cashSales":"10.0000","nonCash":[]}""", Ctx.CashSessionTotals)!;
        Assert.Empty(empty.NonCash!);
        var filled = JsonSerializer.Deserialize("""{"cashSales":"10.0000","nonCash":{"TRANSFER":"3500.0000"}}""", Ctx.CashSessionTotals)!;
        Assert.Equal(3500m, filled.NonCash!["TRANSFER"]);
    }

    [Theory]
    [InlineData("http", "ws")]
    [InlineData("https", "wss")]
    [InlineData("ws", "ws")]
    [InlineData("wss", "wss")]
    public void RealtimeUri_MapsTheNodesHttpSchemeToAWebSocketScheme(string advertised, string expected)
    {
        // system/info reports realtime.scheme "http"; ClientWebSocket only accepts ws/wss.
        var uri = R007.Pos.Core.Realtime.RealtimeClient.BuildUri(new RealtimeInfo(advertised, "127.0.0.1", 8081, "r007-local-key"));
        Assert.Equal(expected, uri.Scheme);
        Assert.Equal("/app/r007-local-key", uri.AbsolutePath);
    }

    [Fact]
    public async Task Ping_UsesTheServerRootHealthProbe_NotApiV1()
    {
        using var pos = new TestPos();
        Assert.True(await pos.Env.Api.PingAsync());
        var call = Assert.Single(pos.Server.Requests, r => r.Path.EndsWith("health/live", StringComparison.Ordinal));
        Assert.Equal("/health/live", call.Path);
    }

    [Fact]
    public async Task Enrolment_SendsTheDeviceMode_AndTheServerCheckReadsTheLowerCasePosKey()
    {
        using var pos = new TestPos();
        await pos.Ctx.RegisterAsync(new Uri("http://mock.local/"), "RESTAURANT", "T");
        var register = Assert.Single(pos.Server.Requests, r => r.Path == "/api/v1/devices/register");
        Assert.Contains("\"mode\":\"POS\"", register.Body, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"POS_TERMINAL\"", register.Body, StringComparison.Ordinal);

        pos.Server.MinPosVersion = "9.0.0";
        var check = await pos.Ctx.CheckServerAsync();
        Assert.False(check.Ok); // minClientVersion.pos is honoured
    }

    [Fact]
    public async Task PayAfterService_PaymentIsOfferedOnlyOnceTheOrderIsServed()
    {
        using var pos = new TestPos();
        pos.Server.UsePaymentTiming(MockData.RestaurantFacilityId, PaymentTimings.PayBeforeLeaving);
        await pos.SignInAsync("RESTAURANT");
        Assert.True(pos.Ctx.Features.PayAfterService);
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Jollof)));

        Assert.False(sell.CanPayOrder);
        Assert.True(sell.AwaitingService);
        await sell.SendCommand.ExecuteAsync();
        Assert.False(sell.CanPayOrder);
        Assert.True(sell.CanServe);
        Assert.Contains("mark the order served", sell.StatusBanner, StringComparison.Ordinal);

        await sell.ServeCommand.ExecuteAsync();
        Assert.Equal(OrderStatuses.Served, sell.Order!.Status);
        Assert.True(sell.CanPayOrder);

        pos.Navigator.Script = async m =>
        {
            var pay = (PaymentViewModel)m;
            pay.Method = TenderTypes.Cash;
            pay.AddTenderCommand.Execute(null);
            await pay.PayCommand.ExecuteAsync();
        };
        await sell.PayCommand.ExecuteAsync();
        Assert.False(sell.HasOrder);
    }

    [Fact]
    public async Task PayAfterService_NodeRefusesPaymentBeforeService_WithOrderStateInvalid()
    {
        using var pos = new TestPos();
        pos.Server.UsePaymentTiming(MockData.RestaurantFacilityId, PaymentTimings.PayAfterService);
        await pos.SignInAsync("RESTAURANT");
        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Jollof, 1)]), IdempotencyKeys.New());

        var ex = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.CreatePaymentAsync(new CreatePaymentRequest(pos.Ctx.FacilityId, [new AllocationInput(order.Id, order.Total)], [new TenderInput(TenderTypes.Transfer, order.Total, "TRF-1")]), IdempotencyKeys.New()));
        Assert.Equal("order_state_invalid", ex.Code);
    }

    [Fact]
    public async Task PayFirst_PaidOrderStaysDraftOnTheNode_ButTheCartMovesOn()
    {
        using var pos = new TestPos();
        pos.Server.UsePaymentTiming(MockData.ReceptionFacilityId, PaymentTimings.PayFirst);
        await pos.SignInAsync("RECEPTION", "S-1001", MockData.CashierPin);
        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Biscuits)));
        var orderId = sell.Order!.Id;
        Assert.True(sell.CanPayOrder);

        pos.Navigator.Script = async m =>
        {
            var pay = (PaymentViewModel)m;
            pay.Method = TenderTypes.Cash;
            pay.AddTenderCommand.Execute(null);
            await pay.PayCommand.ExecuteAsync();
        };
        await sell.PayCommand.ExecuteAsync();

        Assert.Equal(OrderStatuses.Draft, pos.Server.PeekOrder(orderId)!.Status); // like the node: not SETTLED
        Assert.False(sell.HasOrder); // ...but nothing is owed, so the cart is cleared
    }

    [Fact]
    public async Task PercentDiscount_IsSentWithAtMostTwoDecimals()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("RESTAURANT", "S-1003", MockData.SupervisorPin);
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "loyal";
            modal.Value = "12.345"; // 3 decimals: the node's regex would 422
            await modal.SubmitCommand.ExecuteAsync();
            Assert.NotNull(modal.Error);
            modal.Value = "10";
            await modal.SubmitCommand.ExecuteAsync();
        };
        await sell.DiscountCommand.ExecuteAsync(sell.Lines[0]);

        var call = Assert.Single(pos.Server.Requests, r => r.Path.EndsWith("/adjustments", StringComparison.Ordinal));
        Assert.Contains("\"value\":\"10\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_PartPayment_ShowsTenderedChangeAndBalanceDue_AndTheNodeDuplicateMark()
    {
        var receipt = new Receipt(
            Guid.NewGuid(), "RCP-1", null, "Main Reception", "007 Resort & Spa", "", DateTimeOffset.UtcNow, "Ngozi", ["R-1"], null,
            [new ReceiptItem("Sports drink", 5, 700m, 3500m)], 3500m, 0m, 0m, 3500m, "NGN",
            [new ReceiptTender(TenderTypes.Cash, 1000m, null, 2000m)], 1000m, null, null, 1, "Thanks",
            AmountPaid: 1000m, BalanceDue: 2500m, Duplicate: true, BusinessName: "007 Resort & Spa (demo organization)", Terminal: "Till 1");

        var text = R007.Pos.Devices.Printing.EscPosRenderer.RenderText(ReceiptDocumentBuilder.Build(receipt));

        Assert.Contains("*** DUPLICATE ***", text, StringComparison.Ordinal);
        Assert.Contains("  Tendered", text, StringComparison.Ordinal);
        Assert.Contains("N2,000.00", text, StringComparison.Ordinal);
        Assert.Contains("BALANCE DUE", text, StringComparison.Ordinal);
        Assert.Contains("N2,500.00", text, StringComparison.Ordinal);
        Assert.Contains("(demo organization)", text, StringComparison.Ordinal);
        Assert.Contains("Terminal: Till 1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reception_HoldBuildsTheSlotFeeOrder_AttachesIt_AndPayingConfirmsThroughPayments()
    {
        using var pos = new TestPos();
        pos.Server.UsePaymentTiming(MockData.ReceptionFacilityId, PaymentTimings.PayFirst);
        await pos.SignInAsync("RECEPTION");
        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
        var reception = new ReceptionViewModel(pos.Ctx, pos.Navigator);
        await reception.ActivateAsync();
        reception.Resource = reception.Resources[0];
        await reception.LoadSlotsCommand.ExecuteAsync();
        reception.Slot = reception.Slots[1];

        await reception.HoldCommand.ExecuteAsync();

        Assert.Equal(BookingStatuses.PendingPayment, reception.Booking!.Status); // the node's status once the order is attached
        Assert.NotNull(reception.Booking.OrderId);
        var hold = Assert.Single(pos.Server.Requests, r => r.Path == "/api/v1/bookings/hold");
        Assert.DoesNotContain("quantity", hold.Body, StringComparison.Ordinal); // a TIME_SLOT court is not booked by party size
        Assert.Contains(pos.Server.Requests, r => r.Path.EndsWith("/order", StringComparison.Ordinal) && r.Path.Contains("/bookings/", StringComparison.Ordinal));

        pos.Navigator.Script = async m =>
        {
            var pay = (PaymentViewModel)m;
            pay.Method = TenderTypes.Cash;
            pay.TenderedText = "6000";
            pay.AddTenderCommand.Execute(null);
            await pay.PayCommand.ExecuteAsync();
        };
        await reception.PayCommand.ExecuteAsync();

        Assert.DoesNotContain(pos.Server.Requests, r => r.Path.EndsWith("/confirm", StringComparison.Ordinal)); // paid through POST /payments (receipt + change)
        Assert.Contains(pos.Server.Requests, r => r.Path == "/api/v1/payments");
        Assert.StartsWith("ENT.", pos.Printer.PrintedDocuments.Last().QrData, StringComparison.Ordinal);
    }
}
