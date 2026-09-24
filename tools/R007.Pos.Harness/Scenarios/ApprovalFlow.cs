using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Screens;

namespace R007.Pos.Harness;

/// <summary>
/// Cashier requests a void / discount -> the node holds it (202 + Approval) -> a supervisor decides on THEIR terminal (inbox) or
/// stands at the till and steps up with <c>X-Step-Up-Token</c>. Each path is driven through the real SensitiveActionViewModel.
/// </summary>
public static class ApprovalFlow
{
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("approval-void-approved", "Cashier void of a sent order -> 202 -> supervisor inbox approves -> VOIDED", VoidApproved),
        new("approval-void-rejected", "Supervisor rejects the void -> order stays SENT, cashier sees the reason", VoidRejected),
        new("approval-stepup", "Supervisor at the till: step-up token applies the void at once", VoidStepUp),
        new("approval-discount", "Line discount needs approval; the discounted total is the server's", Discount),
        new("approval-cancel", "Cashier cancels a waiting request; a late decision is refused", Cancel),
        new("realtime-approval", "Reverb: POS RealtimeClient subscribes to private-device.{id}; the approval decision is pushed as a hint", Realtime),
    ];

    private static async Task<(PosRig Cashier, PosRig Supervisor)> RigsAsync(NodeContext node)
    {
        var cashier = await PosRig.EnrolAsync(node, "RESTAURANT");
        await cashier.SignInAsync("S-0006");
        await cashier.FreshCashSessionAsync();
        var supervisor = await PosRig.EnrolAsync(node, "RESTAURANT");
        await supervisor.SignInAsync("S-0008");
        return (cashier, supervisor);
    }

    private static async Task<SellViewModel> SentOrderAsync(PosRig rig, int quantity = 2)
    {
        var sell = rig.NewSell();
        var moi = rig.ProductNamed("Moi Moi");
        for (var i = 0; i < quantity; i++)
        {
            await sell.AddProductCommand.ExecuteAsync(new ProductTile(moi));
        }

        await sell.SendCommand.ExecuteAsync();
        Check.Equal(OrderStatuses.Sent, sell.Order!.Status, "order sent: " + sell.Error);
        return sell;
    }

    /// <summary>The supervisor's terminal: waits for the request to show up in the inbox, then approves or rejects it.</summary>
    private static async Task SupervisorDecidesAsync(PosRig supervisor, Guid orderId, string decision, string note)
    {
        var inbox = new ApprovalsInboxViewModel(supervisor.Ctx);
        for (var i = 0; i < 40; i++)
        {
            await inbox.RefreshCommand.ExecuteAsync();
            if (inbox.Pending.FirstOrDefault(r => r.Approval.EntityId == orderId) is { } row)
            {
                Check.Equal("order.void", row.Approval.Action, "approval action");
                Check.True(row.Approval.IsPending, "pending");
                Check.Equal("Emeka Obi", row.Approval.RequestedByName!, "requested-by name");
                inbox.Note = note;
                await (decision == ApprovalDecisions.Approve ? row.ApproveCommand : row.RejectCommand).ExecuteAsync();
                return;
            }

            await Task.Delay(250);
        }

        throw new ScenarioFailure("the void request never reached the supervisor's inbox");
    }

    private static Task<bool> RunVoidAsync(PosRig cashier, SellViewModel sell, Func<SensitiveActionViewModel, Task> script)
    {
        SensitiveActionViewModel? modal = null;
        cashier.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            await script(modal);
        };
        return sell.VoidCommand.ExecuteAsync().ContinueWith(_ => modal!.Outcome == SensitiveOutcome.Applied);
    }

    private static async Task VoidApproved(NodeContext node)
    {
        var (cashier, supervisor) = await RigsAsync(node);
        using var _c = cashier;
        using var _s = supervisor;
        var sell = await SentOrderAsync(cashier);
        var orderId = sell.Order!.Id;

        var decider = Task.CompletedTask;
        var applied = await RunVoidAsync(cashier, sell, async modal =>
        {
            modal.Reason = "Customer left before the food came";
            decider = SupervisorDecidesAsync(supervisor, orderId, ApprovalDecisions.Approve, "ok");
            await modal.SubmitCommand.ExecuteAsync();
        });
        await decider;

        Check.True(applied, "void applied after approval");
        var order = await cashier.Api.GetOrderAsync(orderId);
        Check.Equal(OrderStatuses.Voided, order.Status, "order VOIDED");
        Check.True(!sell.HasOrder, "the cart moved on");
        var pending = await cashier.Api.ListApprovalsAsync("mine", ApprovalStatuses.Pending);
        Check.True(pending.All(a => a.EntityId != orderId), "no pending approval left");
    }

    private static async Task VoidRejected(NodeContext node)
    {
        var (cashier, supervisor) = await RigsAsync(node);
        using var _c = cashier;
        using var _s = supervisor;
        var sell = await SentOrderAsync(cashier);
        var orderId = sell.Order!.Id;
        SensitiveActionViewModel? shown = null;
        var decider = Task.CompletedTask;
        var applied = await RunVoidAsync(cashier, sell, async modal =>
        {
            shown = modal;
            modal.Reason = "Guest changed their mind";
            decider = SupervisorDecidesAsync(supervisor, orderId, ApprovalDecisions.Reject, "Food already cooked");
            await modal.SubmitCommand.ExecuteAsync();
        });
        await decider;

        Check.True(!applied, "void not applied");
        Check.Equal(SensitiveOutcome.Rejected, shown!.Outcome, "outcome");
        Check.Contains(shown.Error ?? string.Empty, "Food already cooked", "the supervisor's note is shown to the cashier");
        Check.Equal(OrderStatuses.Sent, (await cashier.Api.GetOrderAsync(orderId)).Status, "order unchanged");
    }

    private static async Task VoidStepUp(NodeContext node)
    {
        var (cashier, supervisor) = await RigsAsync(node);
        using var _c = cashier;
        using var _s = supervisor;
        var sell = await SentOrderAsync(cashier);
        var orderId = sell.Order!.Id;

        // a wrong supervisor PIN never yields a token
        SensitiveActionViewModel? bad = null;
        var refused = await RunVoidAsync(cashier, sell, async modal =>
        {
            bad = modal;
            modal.Reason = "test";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-0008";
            modal.SupervisorPin = "0000";
            await modal.SubmitCommand.ExecuteAsync();
            modal.CancelCommand.Execute(null);
        });
        Check.True(!refused && bad!.Error is not null, "wrong supervisor PIN: " + bad?.Error);
        Check.Equal(OrderStatuses.Sent, (await cashier.Api.GetOrderAsync(orderId)).Status, "still sent");

        // a supervisor who lacks the permission cannot step up for it (a waiter holds no order.void.approve)
        var waiter = await Check.ThrowsApiAsync(() => cashier.Api.StepUpAsync(new StepUpRequest(CredentialTypes.Pin, "S-0001", "1234", Permissions.OrderVoidApprove)), "waiter step-up");
        Check.True(waiter.Code is "permission_denied" or "step_up_denied" or "insufficient_permission" || waiter.Status == System.Net.HttpStatusCode.Forbidden, $"waiter cannot approve voids (got {waiter.Status} {waiter.Code})");

        var applied = await RunVoidAsync(cashier, sell, async modal =>
        {
            modal.Reason = "Wrong table";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-0008";
            modal.SupervisorPin = "1234";
            await modal.SubmitCommand.ExecuteAsync();
        });
        Check.True(applied, "void applied with the supervisor's step-up token");
        Check.Equal(OrderStatuses.Voided, (await cashier.Api.GetOrderAsync(orderId)).Status, "order VOIDED");
    }

    private static Task ApproveWhenPendingAsync(PosRig supervisor, string actionContains) => Task.Run(async () =>
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-5); // ignore stale requests left by earlier runs
        var inbox = new ApprovalsInboxViewModel(supervisor.Ctx);
        for (var i = 0; i < 40; i++)
        {
            await inbox.RefreshCommand.ExecuteAsync();
            if (inbox.Pending.FirstOrDefault(r => r.Approval.RequestedAt >= since && (r.Approval.RequiredPermission ?? r.Approval.Action).Contains(actionContains, StringComparison.Ordinal)) is { } row)
            {
                await row.ApproveCommand.ExecuteAsync();
                return;
            }

            await Task.Delay(250);
        }

        throw new ScenarioFailure($"the {actionContains} request never reached the inbox");
    });

    private static async Task Discount(NodeContext node)
    {
        var (cashier, supervisor) = await RigsAsync(node);
        using var _c = cashier;
        using var _s = supervisor;
        var sell = await SentOrderAsync(cashier, 1);
        var line = sell.Lines.Single();
        var before = sell.Order!.Total!.Value;

        // The Restaurant lists order.discount and order.comp in require_approval_for: even a small discount is held for a supervisor.
        var decider = Task.CompletedTask;
        cashier.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Loyal guest";
            modal.Value = "10";
            decider = ApproveWhenPendingAsync(supervisor, "discount");
            await modal.SubmitCommand.ExecuteAsync();
            Check.Equal(SensitiveOutcome.Applied, modal.Outcome, "discount outcome (" + modal.Error + ")");
        };
        await sell.DiscountCommand.ExecuteAsync(line);
        await decider;
        var after = await cashier.Api.GetOrderAsync(sell.Order!.Id);
        Check.Equal(before * 0.9m, after.Total, "10% off, computed by the server");
        Check.Equal(before * 0.1m, after.DiscountTotal, "discountTotal is the server's");
        Check.Equal(before * 0.9m, sell.Order.Total!.Value, "the cart shows the server's discounted total");

        // a percentage with more than 2 decimals is refused by the dialog itself (the node would 422)
        cashier.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "x";
            modal.Value = "12.345";
            await modal.SubmitCommand.ExecuteAsync();
            Check.True(modal.Error is not null, "3-decimal percentage refused client-side");
            modal.CancelCommand.Execute(null);
        };
        await sell.DiscountCommand.ExecuteAsync(sell.Lines.Single());

        cashier.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Manager's guest";
            decider = ApproveWhenPendingAsync(supervisor, "comp");
            await modal.SubmitCommand.ExecuteAsync();
        };
        await sell.CompCommand.ExecuteAsync(sell.Lines.Single());
        await decider;
        var comped = await cashier.Api.GetOrderAsync(sell.Order.Id);
        Check.Equal(0m, comped.Total, "comp: the server prices the line at zero");
    }

    private static async Task Cancel(NodeContext node)
    {
        var (cashier, supervisor) = await RigsAsync(node);
        using var _c = cashier;
        using var _s = supervisor;
        var sell = await SentOrderAsync(cashier);
        var orderId = sell.Order!.Id;
        SensitiveActionViewModel? shown = null;
        var applied = await RunVoidAsync(cashier, sell, async modal =>
        {
            shown = modal;
            modal.Reason = "oops";
            var submit = modal.SubmitCommand.ExecuteAsync();
            for (var i = 0; i < 40 && !modal.IsWaiting; i++)
            {
                await Task.Delay(100);
            }

            Check.True(modal.IsWaiting, "the dialog waits for a supervisor");
            await modal.CancelCommand.ExecuteAsync();
            await submit;
        });
        Check.True(!applied, "cancelled");
        Check.Equal(SensitiveOutcome.Cancelled, shown!.Outcome, "outcome");
        Check.Equal(OrderStatuses.Sent, (await cashier.Api.GetOrderAsync(orderId)).Status, "order unchanged");

        var mine = await cashier.Api.ListApprovalsAsync("mine", ApprovalStatuses.Cancelled);
        var approval = mine.First(a => a.EntityId == orderId);
        Check.Equal(ApprovalStatuses.Cancelled, approval.Status, "approval CANCELLED");
        var late = await Check.ThrowsApiAsync(async () => await supervisor.Api.DecideApprovalAsync(approval.Id, new ApprovalDecisionRequest(ApprovalDecisions.Approve), IdempotencyKeys.New()), "late decision");
        Check.Equal("approval_already_decided", late.Code, "late decision code");
    }

    private static async Task Realtime(NodeContext node)
    {
        var (cashier, supervisor) = await RigsAsync(node);
        using var _c = cashier;
        using var _s = supervisor;
        var check = await cashier.Ctx.CheckServerAsync();
        var info = Check.NotNull(check.Info?.Realtime, "system/info advertises the Reverb endpoint");
        node.Log($"reverb at {info.Scheme}://{info.Host}:{info.Port} key {info.AppKey}");

        var events = new System.Collections.Concurrent.ConcurrentQueue<R007.Pos.Core.Realtime.RealtimeEvent>();
        var subscribed = new TaskCompletionSource();
        cashier.Ctx.Realtime.EventReceived += e => events.Enqueue(e);
        cashier.Ctx.Realtime.Subscribed += () => subscribed.TrySetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = cashier.Ctx.Realtime.RunAsync(info, cashier.Ctx.Identity!.DeviceId, cts.Token);
        var first = await Task.WhenAny(subscribed.Task, run, Task.Delay(TimeSpan.FromSeconds(15)));
        Check.True(first == subscribed.Task, "subscribed to private-device.{id} (auth via POST /broadcasting/auth)");

        var sell = await SentOrderAsync(cashier);
        var orderId = sell.Order!.Id;
        var decider = Task.CompletedTask;
        var applied = await RunVoidAsync(cashier, sell, async modal =>
        {
            modal.Reason = "realtime test";
            decider = SupervisorDecidesAsync(supervisor, orderId, ApprovalDecisions.Approve, "ok");
            await modal.SubmitCommand.ExecuteAsync();
        });
        await decider;
        Check.True(applied, "void applied");

        for (var i = 0; i < 40 && !events.Any(e => e.Name.StartsWith("approval", StringComparison.Ordinal)); i++)
        {
            await Task.Delay(250);
        }

        node.Log("events: " + string.Join(", ", events.Select(e => e.Channel + "/" + e.Name)));
        Check.True(events.Any(e => e.Name == "approval.decided"), "an approval.decided hint was pushed to the requesting device");
        await cts.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }
}
