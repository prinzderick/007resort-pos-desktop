using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Offline;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Tests;

public sealed class PaymentTests
{
    private static async Task<(TestPos Pos, SellViewModel Sell)> SellOrderAsync(string code = "RESTAURANT", params Guid[] products)
    {
        var pos = new TestPos();
        await pos.SignInAsync(code);
        var sell = pos.NewSell();
        foreach (var id in products.Length == 0 ? [MockData.Beer, MockData.Jollof] : products)
        {
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(id)));
        }

        return (pos, sell);
    }

    private static PaymentViewModel Modal(TestPos pos, SellViewModel sell) => new(pos.Ctx, PaymentTarget.ForOrder(sell.Order!));

    private static void AddTender(PaymentViewModel m, string method, string amount, string? tendered = null, string? reference = null)
    {
        m.Method = method;
        m.TenderAmountText = amount;
        m.TenderedText = tendered ?? string.Empty;
        m.Reference = reference ?? string.Empty;
        m.AddTenderCommand.Execute(null);
    }

    [Fact]
    public async Task Cash_ChangeComesFromTheServer_ReceiptPrints_DrawerKicks()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);

        AddTender(m, TenderTypes.Cash, "5000.00", tendered: "6000");
        Assert.Null(m.Error);
        Assert.True(m.CanPay);
        await m.PayCommand.ExecuteAsync();

        Assert.True(m.Paid);
        Assert.Equal("Change due: ₦1,000.00", m.ChangeText);
        Assert.Equal(1000m, m.Result!.ChangeDue);
        var doc = Assert.Single(pos.Printer.PrintedDocuments);
        Assert.True(doc.OpenDrawer);
        Assert.Contains(doc.Lines, l => l.Text.Contains("TOTAL", StringComparison.Ordinal) && l.Text.Contains("5,000.00", StringComparison.Ordinal));
        Assert.Equal(OrderStatuses.Settled, pos.Server.PeekOrder(sell.Order!.Id)!.Status);
    }

    [Fact]
    public async Task Tenders_MustAddUpToTheAmountToPay_ElsePayIsDisabled()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);

        AddTender(m, TenderTypes.Cash, "2000");
        Assert.False(m.CanPay);
        Assert.Equal("₦3,000.00", m.RemainingText);

        AddTender(m, TenderTypes.Cash, "3500"); // over the amount
        Assert.Equal("That is more than the amount to pay.", m.Error);
        Assert.False(m.CanPay);

        AddTender(m, TenderTypes.Cash, "3000");
        Assert.True(m.CanPay);
    }

    [Fact]
    public async Task SplitPayment_CashPlusCardPlusTransfer_IsOneAtomicRequest()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Cash, "2000");
        AddTender(m, TenderTypes.PosTerminal, "1500", reference: "RRN-778899");
        AddTender(m, TenderTypes.Transfer, "1500", reference: "TRF-ABC");

        await m.PayCommand.ExecuteAsync();

        Assert.True(m.Paid);
        var call = Assert.Single(pos.Server.Requests, r => r.Method == "POST" && r.Path == "/api/v1/payments");
        Assert.Contains("RRN-778899", call.Body, StringComparison.Ordinal);
        Assert.Equal(3, m.Result!.Payments.Count);
        Assert.Single(m.Result.Payments.Select(p => p.GroupId).Distinct());
        Assert.Equal(5000m, pos.Server.PeekOrder(sell.Order!.Id)!.AmountPaid);
        var receipt = Assert.Single(pos.Printer.PrintedDocuments);
        Assert.Contains(receipt.Lines, l => l.Text.Contains("POS terminal (RRN-778899)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Card_NeedsAReference_AndCashOverpaymentWithoutAmountIsRejected()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);

        AddTender(m, TenderTypes.Card, "5000");
        Assert.Equal("Enter the terminal / transfer reference.", m.Error);

        AddTender(m, TenderTypes.Cash, "5000", tendered: "4000"); // handed over less than owed
        Assert.Equal("Cash handed over must be at least the tender amount.", m.Error);
        Assert.Empty(m.Tenders);
    }

    [Fact]
    public async Task PartialSettle_LeavesTheServerBalance_AndTheOrderOpen()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);
        m.AmountToPayText = "2000";
        AddTender(m, TenderTypes.Cash, "2000");

        await m.PayCommand.ExecuteAsync();

        var order = pos.Server.PeekOrder(sell.Order!.Id)!;
        Assert.Equal(2000m, order.AmountPaid);
        Assert.Equal(3000m, order.BalanceDue);
        Assert.NotEqual(OrderStatuses.Settled, order.Status);
    }

    [Fact]
    public async Task DoubleTapOrLostResponse_NeverChargesTwice_BecauseTheAttemptKeepsItsIdempotencyKey()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Card, "5000", reference: "RRN-1");
        pos.Server.LoseNextResponses = 4; // initial try + all 3 automatic retries: server applied it, we never heard back

        await m.PayCommand.ExecuteAsync();
        Assert.False(m.Paid);                        // card cannot be queued, operator told to retry
        Assert.Equal(1, pos.Server.PaymentCount);    // ...but the first attempt DID reach the server

        await m.PayCommand.ExecuteAsync();           // operator presses Pay again: same key and tender id

        Assert.True(m.Paid);
        Assert.Equal(1, pos.Server.PaymentCount);    // still exactly one payment
        Assert.Equal(5000m, pos.Server.PeekOrder(sell.Order!.Id)!.AmountPaid);
        var keys = pos.Server.Requests.Where(r => r.Path == "/api/v1/payments" && r.Method == "POST").Select(r => r.Headers["Idempotency-Key"]).Distinct().ToList();
        Assert.Single(keys);
    }

    [Fact]
    public async Task EditingTenders_StartsANewAttempt_WithANewKey()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Card, "5000", reference: "RRN-1");
        pos.Server.Offline = true;
        await m.PayCommand.ExecuteAsync();
        pos.Server.Offline = false;
        m.RemoveTenderCommand.Execute(m.Tenders[0]);
        AddTender(m, TenderTypes.Cash, "5000");

        await m.PayCommand.ExecuteAsync();

        Assert.True(m.Paid);
    }

    [Fact]
    public async Task CashSessionRequired_BlocksCashUntilOpened_AndPaymentIsLinkedToIt()
    {
        var (pos, sell) = await SellOrderAsync("CLUB");
        using var _ = pos;
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Cash, "5000");
        Assert.Equal("Open a cash session before taking cash.", m.Error);

        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
        AddTender(m, TenderTypes.Cash, "5000");
        await m.PayCommand.ExecuteAsync();

        Assert.True(m.Paid);
        Assert.Equal(pos.Ctx.CashSession.Id, m.Result!.Payments[0].CashSessionId);
    }

    [Fact]
    public async Task BalanceChangedOnAnotherTerminal_IsExplained_AndNotRetriedBlindly()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var order = sell.Order!;
        // Someone else pays part of it first.
        await pos.Env.Api.CreatePaymentAsync(
            new CreatePaymentRequest(order.FacilityId, [new AllocationInput(order.Id, 1000m)], [new TenderInput(TenderTypes.Card, 1000m, "OTHER")]),
            IdempotencyKeys.New());
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Card, "5000", reference: "RRN-2");

        await m.PayCommand.ExecuteAsync();

        Assert.False(m.Paid);
        Assert.Equal("The balance changed. Please check the amount and try again.", m.Error);
    }

    [Fact]
    public async Task Emergency_CashWhileOffline_IsQueuedNotPaid_AndTellsTheOperatorSo()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        pos.Emergency.Policy = new OfflinePolicy(true, OfflinePaymentPolicy.CashOnly);
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Cash, "5000");
        pos.Server.Offline = true;

        await m.PayCommand.ExecuteAsync();

        Assert.True(m.QueuedPending);
        Assert.False(m.Paid);
        Assert.Contains("NOT CONFIRMED", m.Info, StringComparison.Ordinal);
        Assert.Empty(pos.Printer.PrintedDocuments); // no receipt for money the server has not confirmed
        Assert.Equal(1, await pos.Queue.CountPendingAsync());
        Assert.Equal(0, pos.Server.PaymentCount);
    }

    [Fact]
    public async Task Paystack_PayLink_InitialiseThenVerifyByReference()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        var m = Modal(pos, sell);

        m.PaystackEmail = "guest@example.com";
        await m.PaystackStartCommand.ExecuteAsync();
        Assert.StartsWith("https://checkout.paystack.example/", m.PaystackUrl, StringComparison.Ordinal);

        await m.PaystackVerifyCommand.ExecuteAsync();
        Assert.False(m.Paid);
        Assert.Contains("Not paid yet", m.PaystackStatus, StringComparison.Ordinal);
        Assert.NotEqual(OrderStatuses.Settled, pos.Server.PeekOrder(sell.Order!.Id)!.Status);

        pos.Server.ConfirmProviderPayment(m.PaystackReference!);
        await m.PaystackVerifyCommand.ExecuteAsync();

        Assert.True(m.Paid);
        Assert.Equal(OrderStatuses.Settled, pos.Server.PeekOrder(sell.Order!.Id)!.Status);
    }

    [Fact]
    public async Task Sell_PayFlow_RefreshesTheCart_AndClearsItWhenSettled()
    {
        var (pos, sell) = await SellOrderAsync();
        using var _ = pos;
        pos.Navigator.Script = m =>
        {
            var pay = (PaymentViewModel)m;
            AddTender(pay, TenderTypes.Cash, "5000", tendered: "5000");
            return pay.PayCommand.ExecuteAsync();
        };

        await sell.PayCommand.ExecuteAsync();

        Assert.Null(sell.Order);
        Assert.Empty(sell.Lines);
        Assert.Equal("Paid.", sell.Info);
    }

    [Fact]
    public async Task Reception_TicketsAndRentals_PaidAtThePosPrintAQrEntitlementReceipt()
    {
        var (pos, sell) = await SellOrderAsync("RECEPTION", MockData.PoolTicket, MockData.Racket);
        using var _ = pos;
        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
        var m = Modal(pos, sell);
        AddTender(m, TenderTypes.Cash, "4000");

        await m.PayCommand.ExecuteAsync();

        Assert.True(m.Paid);
        var qrReceipt = pos.Printer.PrintedDocuments.Last();
        Assert.StartsWith("ENT.", qrReceipt.QrData, StringComparison.Ordinal);
        Assert.Contains("QR entitlement receipt", m.PrintMessage, StringComparison.Ordinal);
    }
}
