using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Tests;

public sealed class TabsReceptionCashHistoryTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }

    // Tables & open tabs -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Club_OpenATab_AddTwoRounds_SendEach_SettleOnExit()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("CLUB");
        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
        var tables = new TablesViewModel(pos.Ctx, pos.Navigator);
        SellTarget? chosen = null;
        tables.TargetChosen += (_, t) => chosen = t;
        await tables.ActivateAsync();
        Assert.Equal(8, tables.Tables.Count);

        pos.Navigator.Script = async m =>
        {
            var open = (OpenTabViewModel)m;
            open.CustomerName = "Chief Obi";
            open.Table = open.FreeTables.First(t => t.Label == "T3");
            await open.OpenCommand.ExecuteAsync();
        };
        await tables.OpenTabCommand.ExecuteAsync();

        Assert.NotNull(chosen?.Tab);
        var sell = pos.NewSell();
        await sell.SetTargetAsync(chosen!);
        Assert.Equal("Tab Chief Obi / T3", sell.TargetLabel);

        // Round 1: two beers, sent to the bar
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));
        await sell.IncrementCommand.ExecuteAsync(sell.Lines[0]);
        await sell.SendCommand.ExecuteAsync();
        Assert.Equal(OrderStatuses.Sent, sell.Order!.Status);
        Assert.Equal("Tab balance ₦3,000.00", sell.TabBalanceText);

        // Round 2: a soda, sent
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Soda)));
        await sell.SendCommand.ExecuteAsync();
        Assert.Equal("Tab balance ₦3,500.00", sell.TabBalanceText);
        Assert.Equal(2, (await pos.Env.Api.GetTabAsync(chosen!.Tab!.Id)).OrderIds.Count);

        // Settle on exit: cash + card in one go
        pos.Navigator.Script = async m =>
        {
            var pay = (PaymentViewModel)m;
            Assert.Equal("₦3,500.00", pay.AmountDueText);
            Assert.False(pay.CanEditAmount); // a tab settles in full
            pay.Method = TenderTypes.Cash;
            pay.TenderAmountText = "2000";
            pay.AddTenderCommand.Execute(null);
            pay.Method = TenderTypes.Card;
            pay.TenderAmountText = "1500";
            pay.Reference = "RRN-TAB";
            pay.AddTenderCommand.Execute(null);
            await pay.PayCommand.ExecuteAsync();
        };
        await sell.SettleTabCommand.ExecuteAsync();

        var tab = await pos.Env.Api.GetTabAsync(chosen.Tab.Id);
        Assert.Equal("SETTLED", tab.Status);
        Assert.Equal(0m, tab.BalanceDue);
        var freed = (await pos.Env.Api.GetTablesAsync(pos.Ctx.FacilityId)).Single(t => t.Label == "T3");
        Assert.Equal("FREE", freed.Status);
        Assert.Single(pos.Printer.PrintedDocuments); // one settle = one receipt covering both rounds
    }

    [Fact]
    public async Task SettlingATab_WithUnsentDraft_AsksToSendFirst()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("CLUB");
        var tab = await pos.Env.Api.OpenTabAsync(new OpenTabRequest(pos.Ctx.FacilityId, null, "Walk-in"), IdempotencyKeys.New());
        var sell = pos.NewSell();
        await sell.SetTargetAsync(new SellTarget(null, null, tab));
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Beer)));

        await sell.SettleTabCommand.ExecuteAsync();

        Assert.Equal("Send the current order before settling the tab.", sell.Error);
    }

    [Fact]
    public async Task ChoosingAFreeTable_OccupiesIt_AndOrdersCarryTheTable()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var tables = new TablesViewModel(pos.Ctx, pos.Navigator);
        SellTarget? chosen = null;
        tables.TargetChosen += (_, t) => chosen = t;
        await tables.ActivateAsync();

        await tables.SelectTableCommand.ExecuteAsync(tables.Tables.First(t => t.Label == "T5"));
        var sell = pos.NewSell();
        await sell.SetTargetAsync(chosen!);
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Jollof)));

        Assert.Equal("Table T5", sell.TargetLabel);
        Assert.Equal(chosen!.TableId, sell.Order!.TableId);
        Assert.Equal("OCCUPIED", (await pos.Env.Api.GetTablesAsync(pos.Ctx.FacilityId)).Single(t => t.Label == "T5").Status);
    }

    // Reception -----------------------------------------------------------------------------------------------------
    [Fact]
    public async Task Reception_BookHoldRentalPay_ConfirmsAndPrintsQrEntitlementReceipt()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("RECEPTION");
        pos.Ctx.CashSession = await pos.Env.Api.OpenCashSessionAsync(new OpenCashSessionRequest(pos.Ctx.FacilityId, 0m), IdempotencyKeys.New());
        var reception = new ReceptionViewModel(pos.Ctx, pos.Navigator);
        await reception.ActivateAsync();
        Assert.Equal(["Tennis Court 1", "Swimming Pool"], reception.Resources.Select(r => r.Name));

        reception.Resource = reception.Resources[0];
        await WaitUntilAsync(() => reception.Slots.Count == 8);
        reception.Slot = reception.Slots[2];
        await reception.HoldCommand.ExecuteAsync();
        Assert.True(reception.HasBooking);
        Assert.Contains("₦5,000.00 to pay", reception.BookingText, StringComparison.Ordinal);

        await reception.AddRentalCommand.ExecuteAsync(reception.Rentals.First(r => r.Name == "Racket Rental"));
        Assert.Contains("₦6,000.00 to pay", reception.BookingText, StringComparison.Ordinal);

        pos.Navigator.Script = async m =>
        {
            var pay = (PaymentViewModel)m;
            pay.Method = TenderTypes.Cash;
            pay.TenderAmountText = "6000";
            pay.AddTenderCommand.Execute(null);
            await pay.PayCommand.ExecuteAsync();
        };
        await reception.PayCommand.ExecuteAsync();

        Assert.False(reception.HasBooking);
        var receipt = pos.Printer.PrintedDocuments.Last();
        Assert.StartsWith("ENT.", receipt.QrData, StringComparison.Ordinal);
        Assert.True(receipt.OpenDrawer);
        Assert.Contains("Booking confirmed", reception.Info, StringComparison.Ordinal);
        // The slot is now gone for the next customer.
        Assert.False(reception.Slots[2].CanBook);
    }

    [Fact]
    public async Task Reception_LosingTheRace_ExplainsAndRefreshesSlots()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("RECEPTION");
        var reception = new ReceptionViewModel(pos.Ctx, pos.Navigator);
        await reception.ActivateAsync();
        reception.Resource = reception.Resources[0];
        await WaitUntilAsync(() => reception.Slots.Count == 8);
        var slot = reception.Slots[1];
        // Another till takes the same court slot first.
        await pos.Env.Api.HoldBookingAsync(new HoldRequest(reception.Resources[0].Id, slot.Slot.Start, slot.Slot.End), IdempotencyKeys.New());
        reception.Slot = slot;

        await reception.HoldCommand.ExecuteAsync();

        Assert.Equal("That slot was just taken. Pick another.", reception.Error);
        Assert.False(reception.HasBooking);
        Assert.False(reception.Slots[1].CanBook);
    }

    [Fact]
    public async Task Reception_BookingsNeedALiveServer_NotQueued()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("RECEPTION");
        var reception = new ReceptionViewModel(pos.Ctx, pos.Navigator);
        await reception.ActivateAsync();
        reception.Resource = reception.Resources[0];
        await WaitUntilAsync(() => reception.Slots.Count == 8);
        reception.Slot = reception.Slots[0];
        pos.Emergency.Policy = new(true, OfflinePaymentPolicy.All);
        pos.Server.Offline = true;

        await reception.HoldCommand.ExecuteAsync();

        Assert.Contains("live connection", reception.Error, StringComparison.Ordinal);
        Assert.Equal(0, await pos.Queue.CountPendingAsync());
    }

    [Fact]
    public async Task Reception_CustomerLookup_LinksTheMembership()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("RECEPTION");
        var reception = new ReceptionViewModel(pos.Ctx, pos.Navigator);
        pos.Navigator.Script = async m =>
        {
            var lookup = (CustomerLookupViewModel)m;
            lookup.Query = "chidi";
            await lookup.SearchCommand.ExecuteAsync();
            lookup.SelectCommand.Execute(lookup.Results.Single());
        };

        await reception.LookupMemberCommand.ExecuteAsync();

        Assert.Equal("Chidi Okeke", reception.CustomerName);
        Assert.Contains("M-0001", reception.MemberText, StringComparison.Ordinal);
    }

    // Cash sessions --------------------------------------------------------------------------------------------------
    [Fact]
    public async Task CashSession_OpenTakeCashBlindCloseAndShiftReport()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("CLUB");
        var cash = new CashSessionViewModel(pos.Ctx);
        Assert.False(cash.IsOpen);

        cash.OpeningFloatText = "10000";
        await cash.OpenCommand.ExecuteAsync();
        Assert.True(cash.IsOpen);
        Assert.DoesNotContain("expected", cash.StatusText, StringComparison.OrdinalIgnoreCase); // blind: no system figure while open

        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Jollof)));
        var pay = new PaymentViewModel(pos.Ctx, PaymentTarget.ForOrder(sell.Order!));
        pay.Method = TenderTypes.Cash;
        pay.TenderAmountText = "3500";
        pay.AddTenderCommand.Execute(null);
        await pay.PayCommand.ExecuteAsync();

        cash.CountedText = "13400"; // 100 short
        await cash.CloseCommand.ExecuteAsync();

        Assert.False(cash.IsOpen);
        Assert.Contains("system ₦13,500.00", cash.VarianceText, StringComparison.Ordinal);
        Assert.Contains("variance -₦100.00", cash.VarianceText, StringComparison.Ordinal);
        Assert.Contains(cash.ReportLines, l => l.StartsWith("Expected cash: ₦13,500.00", StringComparison.Ordinal));
        Assert.Contains(cash.ReportLines, l => l.StartsWith("CASH: ₦3,500.00 (1)", StringComparison.Ordinal));

        await cash.PrintReportCommand.ExecuteAsync();
        Assert.Contains(pos.Printer.PrintedDocuments.Last().Lines, l => l.Text.StartsWith("Variance:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CashSession_CannotOpenTwice_AndCountMustBeANumber()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("CLUB");
        var cash = new CashSessionViewModel(pos.Ctx);
        await cash.OpenCommand.ExecuteAsync();
        Assert.False(cash.OpenCommand.CanExecute(null));

        cash.CountedText = "lots";
        await cash.CloseCommand.ExecuteAsync();

        Assert.Contains("Count the cash", cash.Error, StringComparison.Ordinal);
        Assert.True(cash.IsOpen);
    }

    // History --------------------------------------------------------------------------------------------------------
    private static async Task<(TestPos Pos, HistoryViewModel History, Payment Payment)> HistoryWithPaymentAsync(string staff = "S-1001", string pin = MockData.CashierPin)
    {
        var pos = new TestPos();
        await pos.SignInAsync(staffNumber: staff, pin: pin);
        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Jollof, 1)]), IdempotencyKeys.New());
        var paid = await pos.Env.Api.CreatePaymentAsync(new CreatePaymentRequest(pos.Ctx.FacilityId, [new AllocationInput(order.Id, 3500m)], [new TenderInput(TenderTypes.Card, 3500m, "RRN-9")]), IdempotencyKeys.New());
        var history = new HistoryViewModel(pos.Ctx, pos.Navigator);
        await history.ActivateAsync();
        return (pos, history, paid.Payments[0]);
    }

    [Fact]
    public async Task History_ListsPayments_AndReprintIsMarkedDuplicate()
    {
        var (pos, history, payment) = await HistoryWithPaymentAsync();
        using var _ = pos;

        var row = Assert.Single(history.Rows);
        Assert.Equal("CARD", row.Tender);
        Assert.True(row.CanReprint);
        await row.ReprintCommand.ExecuteAsync();

        var doc = Assert.Single(pos.Printer.PrintedDocuments);
        Assert.Contains(doc.Lines, l => l.Text.Contains("DUPLICATE", StringComparison.Ordinal));
        Assert.False(doc.OpenDrawer); // a reprint never kicks the drawer
        Assert.Equal(payment.Id, row.Payment.Id);
    }

    [Fact]
    public async Task History_Refund_GoesThroughApproval_AndCompletesWhenTheSupervisorApproves()
    {
        var (pos, history, payment) = await HistoryWithPaymentAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            Assert.Equal("3500", modal.Value); // defaults to the refundable amount
            modal.Reason = "Wrong item";
            modal.Value = "1000";
            var submit = modal.SubmitCommand.ExecuteAsync();
            await WaitUntilAsync(() => modal.IsWaiting);
            pos.Server.DecideApprovalAsSupervisor(pos.Server.PendingApprovalIds.Single(), true);
            await submit;
        };

        await history.Rows[0].RefundCommand.ExecuteAsync();

        Assert.Equal(SensitiveOutcome.Applied, modal!.Outcome);
        Assert.Equal(PaymentStatuses.PartiallyRefunded, (await pos.Env.Api.GetPaymentAsync(payment.Id)).Status);
        Assert.Equal(1000m, (await pos.Env.Api.GetPaymentAsync(payment.Id)).RefundedAmount);
    }

    [Fact]
    public async Task History_Reversal_WithInlineSupervisorStepUp()
    {
        var (pos, history, payment) = await HistoryWithPaymentAsync();
        using var _ = pos;
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Wrong tender";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-1003";
            modal.SupervisorPin = MockData.SupervisorPin;
            await modal.SubmitCommand.ExecuteAsync();
        };

        await history.Rows[0].ReverseCommand.ExecuteAsync();

        Assert.Equal(PaymentStatuses.Reversed, (await pos.Env.Api.GetPaymentAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task Waiter_HasNoHistoryOrRefundOrReprint()
    {
        using var pos = new TestPos();
        await pos.SignInAsync(staffNumber: "S-1002", pin: MockData.WaiterPin);
        var main = new MainViewModel(pos.Ctx, pos.Navigator);

        main.BuildItems();

        Assert.DoesNotContain(main.Items, i => i.Title == "History");
        Assert.DoesNotContain(main.Items, i => i.Title == "Cash session");
        Assert.DoesNotContain(main.Items, i => i.Title == "Approvals");
        Assert.False(pos.Ctx.Features.CanReprint);
        Assert.False(pos.Ctx.Features.CanRefund);
    }

    // Supervisor inbox -----------------------------------------------------------------------------------------------
    [Fact]
    public async Task SupervisorInbox_ListsApprovableRequests_AndDecides()
    {
        using var pos = new TestPos();
        await pos.SignInAsync(); // cashier requests a void
        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]), IdempotencyKeys.New());
        var pending = await pos.Env.Api.VoidOrderAsync(order.Id, order.RowVersion, new VoidRequest("Customer left"), IdempotencyKeys.New());

        // Supervisor signs in on their own session and sees it.
        pos.Env.Auth.SignOut();
        var login = await pos.Env.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, "S-1003", MockData.SupervisorPin));
        pos.Env.Auth.SignIn(login, pos.Env.Time.GetUtcNow());
        pos.Ctx.RecomputeFeatures();
        var inbox = new ApprovalsInboxViewModel(pos.Ctx);
        await inbox.ActivateAsync();

        var row = Assert.Single(inbox.Pending);
        Assert.Contains("Void order", row.Summary, StringComparison.Ordinal);
        Assert.Equal("Customer left", row.Reason);
        inbox.Note = "OK";
        await row.ApproveCommand.ExecuteAsync();

        Assert.Empty(inbox.Pending);
        Assert.Equal(OrderStatuses.Voided, pos.Server.PeekOrder(order.Id)!.Status);
        Assert.Equal(ApprovalStatuses.Approved, (await pos.Env.Api.GetApprovalAsync(pending.Pending!.Approval.Id)).Status);
    }

    [Fact]
    public async Task Requester_CannotApproveOwnRequest()
    {
        using var pos = new TestPos();
        await pos.SignInAsync("RESTAURANT", "S-1003", MockData.SupervisorPin); // supervisor holds approve but is also the requester
        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]), IdempotencyKeys.New());
        // Supervisors are pre-approved for their own voids, so use a request that only a *different* approver can decide: make it as cashier, decide as cashier.
        pos.Env.Auth.SignOut();
        var login = await pos.Env.Api.LoginAsync(new StaffLoginRequest(CredentialTypes.Pin, "S-1001", MockData.CashierPin));
        pos.Env.Auth.SignIn(login, pos.Env.Time.GetUtcNow());
        var pending = await pos.Env.Api.VoidOrderAsync(order.Id, order.RowVersion, new VoidRequest("x"), IdempotencyKeys.New());

        var ex = await Assert.ThrowsAsync<ApiException>(() => pos.Env.Api.DecideApprovalAsync(pending.Pending!.Approval.Id, new ApprovalDecisionRequest(ApprovalDecisions.Approve), IdempotencyKeys.New()));

        Assert.True(ex.IsPermissionDenied);
    }
}
