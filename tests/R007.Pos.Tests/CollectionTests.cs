using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.Devices.Printing;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Tests;

/// <summary>The cashier side of waiter collection: pre-bill, 'Collected by waiters' inbox, confirm / reject, cash handover, state chips.</summary>
public sealed class CollectionTests
{
    private static string Text(ReceiptDocument d) => string.Join("\n", d.Lines.Select(l => l.Text));

    /// <summary>A cashier POS at the Restaurant with a table order (2 x Star = 3000) sent and on the Sell screen.</summary>
    private static async Task<(TestPos Pos, SellViewModel Sell, WaiterSim Waiter)> BilledAsync(string staff = "S-1001", string pin = MockData.CashierPin)
    {
        var pos = new TestPos();
        await pos.SignInAsync("RESTAURANT", staff, pin);
        var waiter = await WaiterSim.StartAsync(pos);
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));
        await sell.IncrementCommand.ExecuteAsync(sell.Lines[0]);
        await sell.SendCommand.ExecuteAsync();
        Assert.Equal(OrderStatuses.Sent, sell.Order!.Status);
        return (pos, sell, waiter);
    }

    private static async Task<CollectionsInboxViewModel> InboxAsync(TestPos pos)
    {
        var inbox = new CollectionsInboxViewModel(pos.Ctx, pos.Navigator);
        await inbox.ActivateAsync();
        return inbox;
    }

    // Pre-bill ----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task PrintBill_FreezesTheOrder_PrintsAnObviousNonReceipt_AndCountsReprints()
    {
        var (pos, sell, _) = await BilledAsync();
        using var _ = pos;
        Assert.True(sell.CanPrintBill);

        await sell.PrintBillCommand.ExecuteAsync();

        Assert.Null(sell.Error);
        Assert.True(sell.IsBilled);
        Assert.Equal("Bill printed x1", sell.BillText);
        Assert.Contains(sell.StateChips, c => c.Text == OrderStateChips.BillPrinted);
        Assert.Contains(sell.StateChips, c => c.Text == OrderStateChips.AwaitingPayment);
        var text = Text(Assert.Single(pos.Printer.PrintedDocuments));
        Assert.Contains(PreBillDocumentBuilder.TopBanner, text, StringComparison.Ordinal);
        Assert.Contains(PreBillDocumentBuilder.BottomBanner, text, StringComparison.Ordinal);
        Assert.Contains("TOTAL DUE", text, StringComparison.Ordinal);
        Assert.Contains("3,000.00", text, StringComparison.Ordinal);
        Assert.Contains("Waiter:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Receipt R-", text, StringComparison.Ordinal);
        Assert.False(pos.Printer.PrintedDocuments[0].OpenDrawer);

        // frozen: no more lines / send / discount / void; the next item starts a new order
        Assert.False(sell.CanEditLines || sell.CanSend || sell.CanVoid || sell.CanDiscount);
        var raw = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.AddLineAsync(sell.Order!.Id, sell.Order.RowVersion, new OrderLineInput(MockData.Soda, 1), IdempotencyKeys.New()));
        Assert.Equal("order_billed", raw.Code);

        await sell.PrintBillCommand.ExecuteAsync();
        Assert.Equal("Bill printed x2", sell.BillText);
        Assert.Contains("REPRINT #2", Text(pos.Printer.PrintedDocuments[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void PreBillDocument_CarriesThePayLinkQr_AndTaxLines()
    {
        var bill = new PreBill("PRE_BILL", "BILL - NOT A RECEIPT", Guid.NewGuid(), "RES-000101", new PreBillFacility(Guid.NewGuid(), "Restaurant"), "T4", new PreBillWaiter(Guid.NewGuid(), "Tunde"),
            new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero), 1, false, [new PreBillLine("Jollof", 2, 3500m, 7000m)], 7000m, 0m, [new PreBillTaxLine("VAT 7.5%", 525m)], 525m, 7525m, 0m, 7525m, "NGN",
            new PayLinkInfo(true, "RES-000101", "https://pay.example/RES-000101", "PAYQR"), null, null);

        var doc = PreBillDocumentBuilder.Build(bill);

        var text = Text(doc);
        Assert.Equal("PAYQR", doc.QrData);
        Assert.Contains("VAT 7.5%", text, StringComparison.Ordinal);
        Assert.Contains("N7,525.00", text, StringComparison.Ordinal);
        Assert.Contains("Table: T4", text, StringComparison.Ordinal);
        Assert.Contains("Pay reference: RES-000101", text, StringComparison.Ordinal);
        Assert.All(doc.Lines, l => Assert.True(l.Text.Length <= 48, l.Text));
    }

    [Fact]
    public async Task ReopeningABill_NeedsASupervisor_AndIsRefusedWhileMoneyIsCollected()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var orderId = sell.Order!.Id;

        // a collection is pending: the bill cannot be reopened (the button is not even offered)
        await waiter.CollectCardAsync(orderId, 1000m, "AP1");
        await sell.RefreshCommand.ExecuteAsync();
        Assert.False(sell.CanReopenBill);
        var blocked = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.CancelBillAsync(orderId, new CancelBillRequest("changed our minds"), IdempotencyKeys.New()));
        Assert.Equal("collections_pending", blocked.Code);
        Assert.Contains("Confirm or reject", ViewModels_Describe(blocked), StringComparison.Ordinal);
    }

    private static string ViewModels_Describe(Exception ex) => R007.Pos.ViewModels.Infrastructure.ScreenViewModel.Describe(ex);

    [Fact]
    public async Task ReopenBill_SupervisorPresent_ReopensAtOnce_AndTheReprintNeedsASupervisorToo()
    {
        var (pos, sell, _) = await BilledAsync();
        using var _ = pos;
        await sell.PrintBillCommand.ExecuteAsync();
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Guest added a round";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-1003";
            modal.SupervisorPin = MockData.SupervisorPin;
            await modal.SubmitCommand.ExecuteAsync();
        };

        await sell.ReopenBillCommand.ExecuteAsync();

        Assert.False(sell.IsBilled);
        Assert.Equal("Bill reopened. Print a new bill when the order is ready.", sell.Info);

        // printing again after a cancelled bill is a supervisor decision (rule pre_bill_requires_supervisor_if_reopened)
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            Assert.Contains("cancelled before", modal.Summary, StringComparison.Ordinal);
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-1003";
            modal.SupervisorPin = MockData.SupervisorPin;
            await modal.SubmitCommand.ExecuteAsync();
        };
        await sell.PrintBillCommand.ExecuteAsync();
        Assert.True(sell.IsBilled);
        Assert.Equal(2, sell.Order!.BillPrintCount);
    }

    // Inbox: confirm / reject -----------------------------------------------------------------------------------------
    [Fact]
    public async Task WaiterCollectsByCardMachine_CashierSeesItWithDetails_ConfirmsIt_OrderSettles_ReceiptPrints()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var orderId = sell.Order!.Id;
        var printedBefore = pos.Printer.PrintedDocuments.Count;

        var collected = await waiter.CollectCardAsync(orderId, 3000m, "APP-7781", "SLP-99");
        Assert.Equal(PaymentStatuses.PendingConfirmation, collected.Payment.Status);
        Assert.Equal(0m, pos.Server.PeekOrder(orderId)!.AmountPaid); // NOT paid until the cashier confirms

        var inbox = await InboxAsync(pos);
        var row = Assert.Single(inbox.Items);
        Assert.Equal("₦3,000.00", row.AmountText);
        Assert.Equal("Card machine", row.TenderText);
        Assert.Equal("Tunde Waiter", row.WaiterText);
        Assert.Contains("APP-7781", row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("SLP-99", row.ReferenceText, StringComparison.Ordinal);
        Assert.Contains("4242", row.ReferenceText, StringComparison.Ordinal);
        Assert.StartsWith("Order ", row.OrderText, StringComparison.Ordinal);
        Assert.Equal(AgeLevel.Normal, row.Level);

        // chips: collected, awaiting confirmation (not just "awaiting payment")
        await sell.RefreshCommand.ExecuteAsync();
        Assert.Contains(sell.StateChips, c => c.Text == OrderStateChips.CollectedAwaitingConfirmation);
        Assert.False(sell.CanPayOrder);
        Assert.Contains("COLLECTED BY WAITER", sell.StatusBanner, StringComparison.Ordinal);

        pos.Navigator.Script = async m =>
        {
            var confirm = (ConfirmCollectionViewModel)m;
            confirm.ReferenceInput = "APP-7781";
            Assert.False(confirm.ReferenceMismatch);
            await confirm.ConfirmCommand.ExecuteAsync();
            Assert.Null(confirm.Error);
        };
        await row.ConfirmCommand.ExecuteAsync();

        Assert.Empty(inbox.Items);
        var settled = pos.Server.PeekOrder(orderId)!;
        Assert.Equal(OrderStatuses.Settled, settled.Status);
        Assert.Equal(3000m, settled.AmountPaid);
        var payment = pos.Server.PeekPayment(collected.Payment.Id)!;
        Assert.Equal(PaymentStatuses.Captured, payment.Status);
        Assert.Equal("APP-7781", payment.Collection!.MatchedReference);
        Assert.Equal(printedBefore + 1, pos.Printer.PrintedDocuments.Count);
        Assert.Contains("Receipt R-", Text(pos.Printer.PrintedDocuments[^1]), StringComparison.Ordinal);
        Assert.Contains("Confirmed", inbox.Info, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmingWithAReferenceThatDoesNotMatch_WarnsFirst_AndOnlyConfirmsOnTheSecondPress()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        await waiter.CollectCardAsync(sell.Order!.Id, 3000m, "APP-1");
        var inbox = await InboxAsync(pos);
        var paymentId = inbox.Items.Single().PaymentId;

        pos.Navigator.Script = async m =>
        {
            var confirm = (ConfirmCollectionViewModel)m;
            confirm.ReferenceInput = "WRONG-REF";
            Assert.True(confirm.ReferenceMismatch);
            await confirm.ConfirmCommand.ExecuteAsync();
            Assert.True(confirm.HasWarning);
            Assert.False(confirm.Confirmed);
            Assert.Equal(PaymentStatuses.PendingConfirmation, pos.Server.PeekPayment(paymentId)!.Status);
            await confirm.ConfirmCommand.ExecuteAsync(); // "confirm anyway"
            Assert.True(confirm.Confirmed);
        };
        await inbox.Items.Single().ConfirmCommand.ExecuteAsync();

        Assert.Equal(PaymentStatuses.Captured, pos.Server.PeekPayment(paymentId)!.Status);
        Assert.Equal("WRONG-REF", pos.Server.PeekPayment(paymentId)!.Collection!.MatchedReference); // recorded for the audit trail
    }

    [Fact]
    public async Task Reject_NeedsAReason_WarnsItAlertsASupervisor_AndLeavesTheBillUnpaid()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var orderId = sell.Order!.Id;
        var collected = await waiter.CollectCardAsync(orderId, 3000m, "APP-2");
        var inbox = await InboxAsync(pos);

        pos.Navigator.Script = async m =>
        {
            var reject = (RejectCollectionViewModel)m;
            Assert.Contains("alerts a supervisor", reject.WarningText, StringComparison.Ordinal);
            await reject.RejectCommand.ExecuteAsync();
            Assert.Contains("reason is required", reject.Error, StringComparison.Ordinal);
            Assert.Equal(PaymentStatuses.PendingConfirmation, pos.Server.PeekPayment(collected.Payment.Id)!.Status);
            reject.Reason = "Slip shows a different amount";
            await reject.RejectCommand.ExecuteAsync();
        };
        await inbox.Items.Single().RejectCommand.ExecuteAsync();

        Assert.Empty(inbox.Items);
        var payment = pos.Server.PeekPayment(collected.Payment.Id)!;
        Assert.Equal(PaymentStatuses.Rejected, payment.Status);
        Assert.Equal("Slip shows a different amount", payment.Collection!.DecisionReason);
        var order = pos.Server.PeekOrder(orderId)!;
        Assert.Equal(0m, order.AmountPaid);
        Assert.True(order.IsBilled);
        Assert.Contains("supervisor has been alerted", inbox.Info, StringComparison.Ordinal);

        // the bill can be paid again the normal way, or collected again
        await sell.RefreshCommand.ExecuteAsync();
        Assert.True(sell.CanPayOrder);
        Assert.Equal([OrderStateChips.BillPrinted, OrderStateChips.AwaitingPayment], sell.StateChips.Select(c => c.Text));
    }

    [Fact]
    public async Task DuplicateConfirm_IsIdempotent_AndRejectAfterConfirmIsExplained()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var collected = await waiter.CollectCardAsync(sell.Order!.Id, 3000m, "APP-3");
        var key = IdempotencyKeys.New();

        var first = await pos.Env.Api.ConfirmCollectionAsync(collected.Payment.Id, new ConfirmCollectionRequest(), key);
        var second = await pos.Env.Api.ConfirmCollectionAsync(collected.Payment.Id, new ConfirmCollectionRequest(), IdempotencyKeys.New());

        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(PaymentStatuses.Captured, second.Payment.Status);
        Assert.Equal(3000m, pos.Server.PeekOrder(sell.Order!.Id)!.AmountPaid); // one capture

        var late = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.RejectCollectionAsync(collected.Payment.Id, new RejectCollectionRequest("too late"), IdempotencyKeys.New()));
        Assert.Equal("payment_state_invalid", late.Code);
        Assert.Contains("already decided", ViewModels_Describe(late), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmingSomethingAlreadyRejected_ClosesTheDialog_ExplainsAndRefreshes()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var collected = await waiter.CollectCardAsync(sell.Order!.Id, 3000m, "APP-4");
        var inbox = await InboxAsync(pos);
        var row = inbox.Items.Single();
        // another cashier rejects it while this one has the dialog open
        await pos.Env.Api.RejectCollectionAsync(collected.Payment.Id, new RejectCollectionRequest("Not on the slip"), IdempotencyKeys.New());

        ConfirmCollectionViewModel? dialog = null;
        pos.Navigator.Script = async m =>
        {
            dialog = (ConfirmCollectionViewModel)m;
            await dialog.ConfirmCommand.ExecuteAsync();
        };
        await row.ConfirmCommand.ExecuteAsync();

        Assert.NotNull(dialog);
        Assert.False(dialog!.Confirmed);
        Assert.Contains("already decided", dialog.Error, StringComparison.Ordinal);
        Assert.Empty(inbox.Items);
    }

    [Fact]
    public async Task CashierCannotTakeDirectPaymentOnABillTheWaiterAlreadyCollected()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var orderId = sell.Order!.Id;
        await waiter.CollectCardAsync(orderId, 3000m, "APP-5");

        // the raw API refuses, with a code the POS turns into an instruction
        var ex = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.CreatePaymentAsync(
            new CreatePaymentRequest(pos.Ctx.FacilityId, [new AllocationInput(orderId, 3000m)], [new TenderInput(TenderTypes.Cash, 3000m, null, 3000m, ClientIds.New())]), IdempotencyKeys.New()));
        Assert.Equal(HttpStatusCode409(), (int)ex.Status);
        Assert.Equal("pending_collection_exists", ex.Code);
        Assert.Contains("confirm or reject it", ViewModels_Describe(ex), StringComparison.OrdinalIgnoreCase);

        // ...and the Sell screen does not even offer Pay
        await sell.RefreshCommand.ExecuteAsync();
        Assert.False(sell.CanPayOrder);
    }

    private static int HttpStatusCode409() => 409;

    [Fact]
    public async Task PartlyCollected_CashierPaysOnlyTheRest_DefaultAmountIsTheCollectable()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        await waiter.CollectCardAsync(sell.Order!.Id, 1000m, "APP-6");
        await sell.RefreshCommand.ExecuteAsync();

        Assert.True(sell.CanPayOrder);
        Assert.Contains("already collected by a waiter", sell.StatusBanner, StringComparison.Ordinal);
        pos.Navigator.Script = async m =>
        {
            var pay = (PaymentViewModel)m;
            Assert.Equal("₦2,000.00", pay.AmountDueText);
            pay.Method = TenderTypes.Cash;
            pay.TenderAmountText = "2000";
            pay.AddTenderCommand.Execute(null);
            await pay.PayCommand.ExecuteAsync();
            Assert.True(pay.Paid, pay.Error);
        };
        await sell.PayCommand.ExecuteAsync();
        Assert.Equal(2000m, pos.Server.PeekOrder(sell.Order!.Id)!.AmountPaid);
    }

    [Fact]
    public async Task WaiterOverCollecting_IsRefused_AndDuplicateReferencesToo()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var over = await Assert.ThrowsAsync<ApiException>(() => waiter.CollectCardAsync(sell.Order!.Id, 3001m, "X1"));
        Assert.Equal("over_collection", over.Code);
        await waiter.CollectCardAsync(sell.Order!.Id, 1000m, "SAME");
        var dup = await Assert.ThrowsAsync<ApiException>(() => waiter.CollectCardAsync(sell.Order!.Id, 1000m, "SAME"));
        Assert.Equal("duplicate_reference", dup.Code);
    }

    // Permissions -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Waiter_CannotConfirm_TheDeskIsHidden_AndTheApiSaysNo()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        var collected = await waiter.CollectCardAsync(sell.Order!.Id, 3000m, "APP-8");

        var denied = await Assert.ThrowsAsync<ApiException>(() => waiter.Api.ConfirmCollectionAsync(collected.Payment.Id, new ConfirmCollectionRequest(), IdempotencyKeys.New()));
        Assert.True(denied.IsPermissionDenied);
        Assert.Equal(PaymentStatuses.PendingConfirmation, pos.Server.PeekPayment(collected.Payment.Id)!.Status);

        // a POS signed in as the waiter shows no collections desk and no handover desk
        var staff = waiter.Login;
        pos.Env.Auth.SignIn(staff, pos.Env.Time.GetUtcNow());
        Assert.False(pos.Ctx.Features.CanConfirmCollections);
        Assert.False(pos.Ctx.Features.ShowHandoverDesk);
        var main = new MainViewModel(pos.Ctx, pos.Navigator);
        main.BuildItems();
        Assert.DoesNotContain(main.Items, i => i.Title is "Collected by waiters" or "Cash handover");
        var inbox = new CollectionsInboxViewModel(pos.Ctx, pos.Navigator);
        Assert.False(inbox.CanConfirm);
    }

    // Age / expiry ----------------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0, AgeLevel.Normal)]
    [InlineData(10, AgeLevel.Normal)]
    [InlineData(15, AgeLevel.Warning)]
    [InlineData(23, AgeLevel.Warning)]
    [InlineData(25, AgeLevel.Critical)]
    [InlineData(29, AgeLevel.Critical)]
    [InlineData(30, AgeLevel.Expired)]
    [InlineData(45, AgeLevel.Expired)]
    public void AgeLevels_WarnAsTheExpiryWindowClosesIn(int minutes, AgeLevel expected)
    {
        var created = new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, CollectionAge.Level(created, created.AddMinutes(30), created.AddMinutes(minutes)));
    }

    [Fact]
    public async Task InboxRowsAge_WithTheClock_AndExpiredCollectionsLeaveTheList()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        await waiter.CollectCardAsync(sell.Order!.Id, 3000m, "APP-10");
        var inbox = await InboxAsync(pos);
        var row = inbox.Items.Single();
        Assert.Equal("just now", row.AgeText);

        pos.Env.Time.Advance(TimeSpan.FromMinutes(26));
        inbox.TickAges();
        Assert.Equal(AgeLevel.Critical, row.Level);
        Assert.Equal("26 min", row.AgeText);
        Assert.Equal("Critical", row.LevelName);

        pos.Env.Time.Advance(TimeSpan.FromMinutes(5)); // the node expires it after 30 minutes
        await inbox.RefreshQuietlyAsync();
        Assert.Empty(inbox.Items);
        Assert.Equal(PaymentStatuses.Expired, pos.Server.PeekPayment(row.PaymentId)!.Status);
        Assert.True(sell.Order is not null);
    }

    // Tables & tabs chips -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task TablesAndTabs_ShowBillPrintedAwaitingPaymentAndCollectedChips()
    {
        var pos = new TestPos();
        using var _ = pos;
        await pos.SignInAsync();
        using var waiter = await WaiterSim.StartAsync(pos);
        var tables = new TablesViewModel(pos.Ctx, pos.Navigator);
        SellTarget? chosen = null;
        tables.TargetChosen += (_, t) => chosen = t;
        await tables.ActivateAsync();
        await tables.SelectTableCommand.ExecuteAsync(tables.Tables.First(t => t.Label == "T2"));
        var sell = pos.NewSell();
        await sell.SetTargetAsync(chosen!);
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Jollof)));
        await sell.SendCommand.ExecuteAsync();
        await tables.ActivateAsync();
        Assert.Empty(tables.Tables.First(t => t.Label == "T2").Chips);

        await sell.PrintBillCommand.ExecuteAsync();
        await tables.ActivateAsync();
        var tile = tables.Tables.First(t => t.Label == "T2");
        Assert.Equal([OrderStateChips.BillPrinted, OrderStateChips.AwaitingPayment], tile.Chips.Select(c => c.Text));

        await waiter.CollectCardAsync(sell.Order!.Id, 3500m, "APP-11");
        await tables.ActivateAsync();
        tile = tables.Tables.First(t => t.Label == "T2");
        Assert.Equal([OrderStateChips.BillPrinted, OrderStateChips.CollectedAwaitingConfirmation], tile.Chips.Select(c => c.Text));
        Assert.Equal("Bill printed | Collected – awaiting confirmation", tile.ChipsText);
        Assert.Empty(tables.Tables.First(t => t.Label == "T3").Chips);

        // returning to the table lists its open order, with the chips, and loads it into the cart
        var again = pos.NewSell();
        await again.SetTargetAsync(new SellTarget(chosen!.TableId, "T2", null));
        var open = Assert.Single(again.OpenOrders);
        Assert.True(open.HasChips);
        await again.SelectOpenOrderCommand.ExecuteAsync(open);
        Assert.True(again.IsBilled);
        Assert.False(again.CanPayOrder);
    }

    [Fact]
    public async Task Tab_ChipsAreTheUnionOfItsOrders()
    {
        var pos = new TestPos();
        using var _ = pos;
        await pos.SignInAsync("CLUB");
        var tab = await pos.Env.Api.OpenTabAsync(new OpenTabRequest(pos.Ctx.FacilityId, null, "Chief"), IdempotencyKeys.New());
        var sell = pos.NewSell();
        await sell.SetTargetAsync(new SellTarget(null, null, tab));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));
        await sell.SendCommand.ExecuteAsync();
        await sell.PrintBillCommand.ExecuteAsync();
        var tables = new TablesViewModel(pos.Ctx, pos.Navigator);

        await tables.ActivateAsync();

        Assert.Equal([OrderStateChips.BillPrinted, OrderStateChips.AwaitingPayment], tables.Tabs.Single().Chips.Select(c => c.Text));
    }

    // Cash handover -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task WaiterCashHolding_IsOffByDefault_TheApiRefusesCash_AndCardStillWorks()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();

        var cash = await Assert.ThrowsAsync<ApiException>(() => waiter.CollectAsync(sell.Order!.Id, new CollectionRequest(CollectionTenders.Cash, 1000m, ClientIds.New(), 1000m)));

        Assert.Equal("cash_holding_not_allowed", cash.Code);
        Assert.Equal(PaymentStatuses.PendingConfirmation, (await waiter.CollectCardAsync(sell.Order!.Id, 1000m, "APP-12")).Payment.Status);
    }

    [Fact]
    public async Task CashHandover_CountedShort_OverTheLimit_NeedsASupervisorSignoff()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        pos.Server.WaiterCashHolding = true;
        await sell.PrintBillCommand.ExecuteAsync();
        var facility = pos.Ctx.FacilityId;
        var cash = await waiter.CollectAsync(sell.Order!.Id, new CollectionRequest(CollectionTenders.Cash, 3000m, ClientIds.New(), 5000m));
        var inbox = await InboxAsync(pos);
        var row = Assert.Single(inbox.Items);
        Assert.Equal("Cash", row.TenderText);
        Assert.Contains("Handed over ₦5,000.00", row.ReferenceText, StringComparison.Ordinal);

        pos.Navigator.Script = async m => await ((ConfirmCollectionViewModel)m).ConfirmCommand.ExecuteAsync();
        await row.ConfirmCommand.ExecuteAsync();
        Assert.Equal(PaymentStatuses.Captured, pos.Server.PeekPayment(cash.Payment.Id)!.Status);

        var handover = await waiter.DeclareHandoverAsync(3000m, facility);
        var desk = new CashHandoverDeskViewModel(pos.Ctx, pos.Navigator);
        await desk.ActivateAsync();
        var handoverRow = Assert.Single(desk.Handovers);
        Assert.Equal("Tunde Waiter", handoverRow.WaiterText);
        Assert.Equal("Waiting to be counted", handoverRow.StatusText);
        var holding = Assert.Single(desk.Holdings);
        Assert.Equal("₦3,000.00", holding.AmountText);

        pos.Navigator.Script = async m =>
        {
            var receive = (ReceiveHandoverViewModel)m;
            receive.CountedText = "2000";
            Assert.Equal("SHORT by ₦1,000.00", receive.VariancePreview);
            await receive.ReceiveCommand.ExecuteAsync();
            Assert.Null(receive.Error);
        };
        await handoverRow.ReceiveCommand.ExecuteAsync();

        var after = pos.Server.PeekPayment(cash.Payment.Id);
        Assert.NotNull(after);
        var pending = Assert.Single(desk.Handovers);
        Assert.Equal(HandoverStatuses.PendingSignoff, pending.Handover.Status);
        Assert.Equal(-1000m, pending.Handover.Variance);
        Assert.Equal("SHORT ₦1,000.00", pending.VarianceText);
        Assert.Contains("supervisor must sign it off", desk.Info, StringComparison.Ordinal);
        Assert.Empty(desk.Holdings); // the declared cash left the waiter's hands
        Assert.False(pending.SignoffCommand.CanExecute(null)); // a cashier cannot sign off
        Assert.False(desk.CanSignoff);

        // the supervisor signs it off on the same desk
        var login = await pos.Env.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, "S-1003", MockData.SupervisorPin));
        pos.Env.Auth.SignIn(login, pos.Env.Time.GetUtcNow());
        await desk.ActivateAsync();
        desk.Note = "Waiter accepted the shortfall";
        await Assert.Single(desk.Handovers).SignoffCommand.ExecuteAsync();
        Assert.Empty(desk.Handovers);
        Assert.Equal(HandoverStatuses.SignedOff, (await pos.Env.Api.ListCashHandoversAsync(facility)).Single(h => h.Id == handover.Id).Status);
    }

    [Fact]
    public async Task CashHandover_SmallVariance_IsReceivedAtOnce_AndAReceivedHandoverCannotBeReceivedTwice()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        pos.Server.WaiterCashHolding = true;
        await sell.PrintBillCommand.ExecuteAsync();
        await waiter.CollectAsync(sell.Order!.Id, new CollectionRequest(CollectionTenders.Cash, 3000m, ClientIds.New(), 3000m));
        var handover = await waiter.DeclareHandoverAsync(3000m, pos.Ctx.FacilityId);

        var first = await pos.Env.Api.ReceiveCashHandoverAsync(handover.Id, new ReceiveHandoverRequest(2900m), IdempotencyKeys.New());
        Assert.Equal(HandoverStatuses.Received, first.Status);
        Assert.Equal(-100m, first.Variance);
        var again = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.ReceiveCashHandoverAsync(handover.Id, new ReceiveHandoverRequest(3000m), IdempotencyKeys.New()));
        Assert.Equal("already_received", again.Code);
        Assert.Contains("already received", ViewModels_Describe(again), StringComparison.Ordinal);
    }

    // Shell: badge & realtime ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task InboxTitleBadge_CountsPendingCollections_ForThoseWhoCanConfirm()
    {
        var (pos, sell, waiter) = await BilledAsync();
        using var _ = pos;
        using var __ = waiter;
        await sell.PrintBillCommand.ExecuteAsync();
        await waiter.CollectCardAsync(sell.Order!.Id, 1000m, "B-1");
        await waiter.CollectCardAsync(sell.Order!.Id, 500m, "B-2");
        var main = new MainViewModel(pos.Ctx, pos.Navigator);
        main.BuildItems();

        await main.RefreshBadgesAsync();

        Assert.Equal("2", main.Items.Single(i => i.Title == "Collected by waiters").Badge);
        Assert.Equal("2", main.CollectionsBadgeText);
        await main.OnCollectionEventAsync("payment.alert", "Collection rejected at T4");
        Assert.Equal("Collection rejected at T4", main.Collections.Alert);
    }

    [Fact]
    public async Task ShellRoutesPaymentEvents_ToTheInbox_AndShowsTheBadge()
    {
        var pos = new TestPos();
        using var _ = pos;
        var shell = new R007.Pos.ViewModels.ShellViewModel(pos.Ctx);
        await shell.StartAsync();
        await pos.SignInAsync();
        using var waiter = await WaiterSim.StartAsync(pos);
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));
        await sell.SendCommand.ExecuteAsync();
        await sell.PrintBillCommand.ExecuteAsync();
        await waiter.CollectCardAsync(sell.Order!.Id, 1500m, "S-1");
        var main = new MainViewModel(pos.Ctx, pos.Navigator);
        main.BuildItems();

        await main.OnCollectionEventAsync("payment.collected", null);

        Assert.Equal(1, main.Collections.PendingCount);
        Assert.Equal("1", main.CollectionsBadgeText);
        Assert.NotNull(shell);
    }
}
