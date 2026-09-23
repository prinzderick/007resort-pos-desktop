using System.Net;
using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>Payments at the Reception counter (PAY_FIRST): cash + change, split, partial, refund with approval, reversal with step-up.</summary>
public static class PaymentFlow
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("pay-first-cash", "PAY_FIRST: a DRAFT order is paid in cash with server-computed change; cart clears; receipt prints", PayFirstCash),
        new("pay-split-partial", "Partial settle, then split cash + transfer for the rest; duplicate transfer reference refused", SplitPartial),
        new("pay-refund-reversal", "Refund needs approval (202 -> supervisor), reversal with step-up; ledger stays immutable", RefundReversal),
    ];

    private static async Task<PosRig> ReceptionAsync(NodeContext node)
    {
        var rig = await PosRig.EnrolAsync(node, "RECEPTION");
        await rig.SignInAsync("S-0005");
        await rig.FreshCashSessionAsync(5000m);
        return rig;
    }

    /// <summary>A Reception counter order of retail goods (no tickets), built through the sell view-model.</summary>
    private static async Task<SellViewModel> DrinkOrderAsync(PosRig rig, int drinks)
    {
        var sell = rig.NewSell();
        var drink = rig.Product("GOODS-SPORTS-DRINK");
        for (var i = 0; i < drinks; i++)
        {
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(drink));
        }

        Check.True(sell.Error is null, "drinks added: " + sell.Error);
        return sell;
    }

    private static async Task PayFirstCash(NodeContext node)
    {
        using var rig = await ReceptionAsync(node);
        var sell = await DrinkOrderAsync(rig, 3); // 3 x 700
        Check.Equal(2100m, sell.Order!.Total!.Value, "server total");
        Check.Equal(OrderStatuses.Draft, sell.Order.Status, "still a draft");
        Check.True(sell.CanPayOrder && !sell.AwaitingService, "PAY_FIRST: a draft order can be paid");

        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "2100.00", "5000.00", null));
        await sell.PayCommand.ExecuteAsync();
        Check.True(sell.Info?.Contains("2,900.00", StringComparison.Ordinal) == true, "change 2900.00 from the server: " + sell.Info);
        Check.True(!sell.HasOrder, "paid in full: the cart is cleared even though the node keeps the order DRAFT");

        var doc = rig.Printer.PrintedDocuments.Last();
        Check.True(doc.OpenDrawer, "drawer kick on cash");
        var text = string.Join("\n", doc.Lines.Select(l => l.Text));
        Check.Contains(text, "2,100.00", "receipt total");
        Check.Contains(text, "2,900.00", "receipt change");
    }

    private static async Task SplitPartial(NodeContext node)
    {
        using var rig = await ReceptionAsync(node);
        var sell = await DrinkOrderAsync(rig, 5); // 3500
        var orderId = sell.Order!.Id;

        // partial settle: only 1000 now
        RestaurantFlow.ScriptPayment(rig, "1000.00", (TenderTypes.Cash, "1000.00", null, null));
        await sell.PayCommand.ExecuteAsync();
        var partial = await rig.Api.GetOrderAsync(orderId);
        Check.Equal(1000m, partial.AmountPaid, "partial payment recorded");
        Check.Equal(2500m, partial.BalanceDue, "server balance after the partial payment");
        Check.True(sell.HasOrder && sell.BalanceText.Contains("2,500.00", StringComparison.Ordinal), "the cart still shows the order with the balance: " + sell.BalanceText);

        // the dialog refuses tenders that exceed the amount, and non-cash needs a reference
        var reference = "TRF-" + Guid.NewGuid().ToString("N")[..10];
        rig.Navigator.Script = m =>
        {
            var pay = (PaymentViewModel)m;
            Check.Contains(pay.AmountDueText, "2,500.00", "the dialog defaults to the server balance");
            pay.Method = TenderTypes.Transfer;
            pay.TenderAmountText = "9999";
            pay.Reference = reference;
            pay.AddTenderCommand.Execute(null);
            Check.True(pay.Error is not null, "over-tender refused");
            pay.CancelCommand.Execute(null);
            return Task.CompletedTask;
        };
        await sell.PayCommand.ExecuteAsync();

        // the rest: split cash + transfer, one atomic request
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "1500.00", "2000.00", null), (TenderTypes.Transfer, "1000.00", null, reference));
        await sell.PayCommand.ExecuteAsync();
        var done = await rig.Api.GetOrderAsync(orderId);
        Check.Equal(3500m, done.AmountPaid, "fully paid");
        Check.Equal(0m, done.BalanceDue, "no balance");
        var payments = (await rig.Api.ListPaymentsAsync(rig.Ctx.FacilityId, rig.Ctx.CashSession!.Id, null, 50)).Items.Where(p => p.Allocations?.Any(a => a.OrderId == orderId) == true).ToList();
        Check.Equal(3, payments.Count, "three payment rows (cash 1000, cash 1500, transfer 1000)");
        Check.Equal(2, payments.Select(p => p.GroupId).Distinct().Count(), "two groups: the partial and the split");
        Check.Equal(500m, payments.Where(p => p.TenderType == TenderTypes.Cash).Sum(p => p.ChangeGiven ?? 0m), "change 500 on the 1500 cash tender");

        // a transfer reference backs one live payment
        var again = await DrinkOrderAsync(rig, 1);
        var dup = await Check.ThrowsApiAsync(() => rig.Api.CreatePaymentAsync(new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(again.Order!.Id, 700m)], [new TenderInput(TenderTypes.Transfer, 700m, reference)], rig.Ctx.CashSession!.Id), IdempotencyKeys.New()), "duplicate reference");
        Check.Equal("duplicate_reference", dup.Code, "duplicate reference code");

        // tenders must equal allocations (raw, bypassing the dialog)
        var mismatch = await Check.ThrowsApiAsync(() => rig.Api.CreatePaymentAsync(new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(again.Order!.Id, 700m)], [new TenderInput(TenderTypes.Cash, 600m, null, 600m)], rig.Ctx.CashSession!.Id), IdempotencyKeys.New()), "amount mismatch");
        Check.Equal("amount_mismatch", mismatch.Code, "amount_mismatch code");
        Check.Equal(HttpStatusCode.UnprocessableEntity, mismatch.Status, "amount_mismatch status");

        // retry safety: the same Idempotency-Key + tender ids replay the ORIGINAL result, no second charge
        var key = IdempotencyKeys.New();
        var request = new CreatePaymentRequest(rig.Ctx.FacilityId, [new AllocationInput(again.Order!.Id, 700m)], [new TenderInput(TenderTypes.Cash, 700m, null, 1000m, ClientIds.New())], rig.Ctx.CashSession!.Id);
        var first = await rig.Api.CreatePaymentAsync(request, key);
        var second = await rig.Api.CreatePaymentAsync(request, key);
        Check.Equal(first.Payments[0].Id, second.Payments[0].Id, "same key -> same payment");
        Check.Equal(700m, (await rig.Api.GetOrderAsync(again.Order.Id)).AmountPaid, "charged once");
        // same client tender id + same body under a NEW key (a queue replay after a lost response) is still a replay, not a second charge
        var replay = await rig.Api.CreatePaymentAsync(request, IdempotencyKeys.New());
        Check.Equal(first.Payments[0].Id, replay.Payments[0].Id, "same tender id + same body -> the original payment");
        Check.Equal(700m, (await rig.Api.GetOrderAsync(again.Order.Id)).AmountPaid, "still charged once");
        var changed = await Check.ThrowsApiAsync(() => rig.Api.CreatePaymentAsync(request with { Tenders = [request.Tenders[0] with { Amount = 700m, Tendered = 2000m }] }, IdempotencyKeys.New()), "same tender id, different body");
        Check.Equal("concurrency_conflict", changed.Code, "same tender id + different body -> 409 concurrency_conflict");
    }

    private static async Task RefundReversal(NodeContext node)
    {
        using var rig = await ReceptionAsync(node);
        using var supervisor = await PosRig.EnrolAsync(node, "RECEPTION");
        await supervisor.SignInAsync("S-0008");

        var sell = await DrinkOrderAsync(rig, 4); // 2800
        var orderId = sell.Order!.Id;
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "2800.00", "2800.00", null));
        await sell.PayCommand.ExecuteAsync();
        var paid = (await rig.Api.ListPaymentsAsync(rig.Ctx.FacilityId, rig.Ctx.CashSession!.Id, null, 50)).Items.Single(p => p.Allocations?.Any(a => a.OrderId == orderId) == true);

        // refund: cashiers need approval -> 202 with the Refund body, supervisor decides in the inbox
        var history = new HistoryViewModel(rig.Ctx, rig.Navigator);
        await history.ActivateAsync();
        var row = history.Rows.First(r => r.Payment.Id == paid.Id);
        Check.True(row.CanRefund && row.CanReprint, "history row offers refund + reprint");

        var decider = Task.CompletedTask;
        rig.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Bottles were warm";
            modal.Value = "700";
            decider = Task.Run(async () =>
            {
                var inbox = new ApprovalsInboxViewModel(supervisor.Ctx);
                for (var i = 0; i < 40; i++)
                {
                    await inbox.RefreshCommand.ExecuteAsync();
                    if (inbox.Pending.FirstOrDefault(r => r.Approval.EntityId == paid.Id) is { } pending)
                    {
                        Check.Equal("payment.refund", pending.Approval.Action, "refund approval action");
                        Check.Equal(700m, pending.Approval.Amount!.Value, "approval amount");
                        await pending.ApproveCommand.ExecuteAsync();
                        return;
                    }

                    await Task.Delay(250);
                }

                throw new ScenarioFailure("the refund never reached the inbox");
            });
            await modal.SubmitCommand.ExecuteAsync();
            Check.Equal(SensitiveOutcome.Applied, modal.Outcome, "refund outcome: " + modal.Error);
        };
        await row.RefundCommand.ExecuteAsync();
        await decider;
        var refunded = await rig.Api.GetPaymentAsync(paid.Id);
        Check.Equal(PaymentStatuses.PartiallyRefunded, refunded.Status, "payment PARTIALLY_REFUNDED");
        Check.Equal(700m, refunded.RefundedAmount!.Value, "refunded amount");

        // over-refund is refused
        var stepUp = await rig.Api.StepUpAsync(new StepUpRequest(CredentialTypes.Pin, "S-0008", "1234", Permissions.RefundApprove, "payment", paid.Id));
        var over = await Check.ThrowsApiAsync(() => rig.Api.RefundPaymentAsync(paid.Id, new RefundRequest(5000m, "too much"), IdempotencyKeys.New(), stepUp.StepUpToken), "over-refund");
        Check.True(over.Status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity, $"over-refund refused ({over.Status} {over.Code})");

        // reversal (wrong tender, same session) with the supervisor at the till
        var second = await DrinkOrderAsync(rig, 2); // 1400
        var secondId = second.Order!.Id;
        RestaurantFlow.ScriptPayment(rig, null, (TenderTypes.Cash, "1400.00", "1400.00", null));
        await second.PayCommand.ExecuteAsync();
        var payment2 = (await rig.Api.ListPaymentsAsync(rig.Ctx.FacilityId, rig.Ctx.CashSession!.Id, null, 50)).Items.Single(p => p.Allocations?.Any(a => a.OrderId == secondId) == true);
        await history.ActivateAsync();
        var row2 = history.Rows.First(r => r.Payment.Id == payment2.Id);
        rig.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Wrong tender";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-0008";
            modal.SupervisorPin = "1234";
            await modal.SubmitCommand.ExecuteAsync();
            Check.Equal(SensitiveOutcome.Applied, modal.Outcome, "reversal outcome: " + modal.Error);
        };
        await row2.ReverseCommand.ExecuteAsync();
        var reversed = await rig.Api.GetPaymentAsync(payment2.Id);
        Check.Equal(PaymentStatuses.Reversed, reversed.Status, "payment REVERSED");
        var reopened = await rig.Api.GetOrderAsync(secondId);
        Check.Equal(1400m, reopened.BalanceDue, "a reversal re-opens the order's balance");

        // the original receipt is an immutable snapshot; a reprint is marked DUPLICATE and counted
        var receipt = await rig.Api.GetReceiptAsync(paid.ReceiptId!.Value);
        Check.Equal(2800m, receipt.Total, "receipt total unchanged by the refund");
        var reprint = await rig.Api.GetReceiptAsync(paid.ReceiptId!.Value, reprint: true);
        Check.True((reprint.ReprintCount ?? 0) >= 1, "reprint counted");
    }
}
