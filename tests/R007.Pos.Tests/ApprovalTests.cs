using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Tests;

public sealed class ApprovalTests
{
    private static async Task<(TestPos Pos, SellViewModel Sell, CartLineViewModel Line)> SellWithLineAsync(string staff = "S-1001", string pin = MockData.CashierPin)
    {
        var pos = new TestPos();
        await pos.SignInAsync(staffNumber: staff, pin: pin);
        var sell = pos.NewSell();
        await sell.AddProductCommand.ExecuteAsync(new ProductTile(pos.Product(MockData.Jollof)));
        return (pos, sell, sell.Lines[0]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }

    [Fact]
    public async Task SmallDiscount_IsWithinTheCashiersAuthority_AndAppliesImmediately()
    {
        var (pos, sell, line) = await SellWithLineAsync();
        using var _ = pos;
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Loyal customer";
            modal.Value = "5";
            await modal.SubmitCommand.ExecuteAsync();
        };

        await sell.DiscountCommand.ExecuteAsync(line);

        Assert.Equal("₦3,325.00", sell.TotalText); // 3500 less 5%, priced by the server
        Assert.Empty(pos.Server.PendingApprovalIds);
    }

    [Fact]
    public async Task LargeDiscount_GoesToApproval_ThenSupervisorApproves_AndTheCartUpdates()
    {
        var (pos, sell, line) = await SellWithLineAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            modal.Reason = "Manager's friend";
            modal.Value = "20";
            var submit = modal.SubmitCommand.ExecuteAsync();
            await WaitUntilAsync(() => modal.IsWaiting);
            Assert.Equal(OrderStatuses.PendingApproval, pos.Server.PeekOrder(sell.Order!.Id)!.Status);
            pos.Server.DecideApprovalAsSupervisor(pos.Server.PendingApprovalIds.Single(), approve: true);
            await submit;
        };

        await sell.DiscountCommand.ExecuteAsync(line);

        Assert.Equal(SensitiveOutcome.Applied, modal!.Outcome);
        Assert.Equal("₦2,800.00", sell.TotalText); // 20% off 3500
        Assert.Equal(OrderStatuses.Draft, sell.Order!.Status);
        Assert.Contains("approved and applied", sell.Info, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupervisorRejects_NothingChanges_AndTheReasonIsShown()
    {
        var (pos, sell, line) = await SellWithLineAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            modal.Reason = "Free lunch";
            var submit = modal.SubmitCommand.ExecuteAsync();
            await WaitUntilAsync(() => modal.IsWaiting);
            pos.Server.DecideApprovalAsSupervisor(pos.Server.PendingApprovalIds.Single(), approve: false, note: "Not allowed");
            await submit;
        };

        await sell.CompCommand.ExecuteAsync(line);

        Assert.Equal(SensitiveOutcome.Rejected, modal!.Outcome);
        Assert.Contains("Not allowed", modal.Error, StringComparison.Ordinal);
        Assert.Equal("₦3,500.00", sell.TotalText);
        Assert.Equal(OrderStatuses.Draft, sell.Order!.Status); // back to draft, not stuck pending
    }

    [Fact]
    public async Task NoSupervisorResponds_TimesOut_AndTheRequestStaysPending()
    {
        var (pos, sell, _) = await SellWithLineAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            modal.Reason = "Spoiled";
            await modal.SubmitCommand.ExecuteAsync(); // the test delay advances the clock 2s per poll until the 10 minute timeout
        };

        await sell.VoidCommand.ExecuteAsync();

        Assert.Equal(SensitiveOutcome.TimedOut, modal!.Outcome);
        Assert.Single(pos.Server.PendingApprovalIds);
        Assert.Contains("still pending", modal.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OperatorCancelsWhileWaiting_ApprovalIsCancelledOnTheServer()
    {
        var (pos, sell, _) = await SellWithLineAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            modal.Reason = "Changed mind";
            var submit = modal.SubmitCommand.ExecuteAsync();
            await WaitUntilAsync(() => modal.IsWaiting);
            await modal.CancelCommand.ExecuteAsync();
            await submit;
        };

        await sell.VoidCommand.ExecuteAsync();

        Assert.Equal(SensitiveOutcome.Cancelled, modal!.Outcome);
        Assert.Empty(pos.Server.PendingApprovalIds);
        Assert.Equal(OrderStatuses.Draft, sell.Order!.Status);
    }

    [Fact]
    public async Task SupervisorPresent_StepUpAuthorisesInline_NoApprovalRequestIsCreated()
    {
        var (pos, sell, _) = await SellWithLineAsync();
        using var _ = pos;
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Customer left";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-1003";
            modal.SupervisorPin = MockData.SupervisorPin;
            await modal.SubmitCommand.ExecuteAsync();
            Assert.Null(modal.Error);
            Assert.Equal(string.Empty, modal.SupervisorPin); // never retained
        };
        var orderId = sell.Order!.Id;

        await sell.VoidCommand.ExecuteAsync();

        Assert.Null(sell.Error);
        Assert.Equal(OrderStatuses.Voided, pos.Server.PeekOrder(orderId)!.Status);
        Assert.Null(sell.Order); // cart cleared
        Assert.Empty(pos.Server.PendingApprovalIds);
        var voidCall = pos.Server.Requests.Single(r => r.Path.EndsWith("/void", StringComparison.Ordinal));
        Assert.StartsWith("su-", voidCall.Headers["X-Step-Up-Token"]);
        // Step-up is authenticated by the requester's own session (the approver's credential rides in the body).
        var stepUp = pos.Server.Requests.Single(r => r.Path.EndsWith("/step-up", StringComparison.Ordinal));
        Assert.StartsWith("Bearer at-", stepUp.Headers["Authorization"]);
    }

    [Fact]
    public async Task StepUp_ByStaffWithoutApprovePermission_IsRefused()
    {
        var (pos, sell, _) = await SellWithLineAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            modal.Reason = "x";
            modal.SupervisorHere = true;
            modal.SupervisorNumber = "S-1001"; // the cashier "approving" themselves
            modal.SupervisorPin = MockData.CashierPin;
            await modal.SubmitCommand.ExecuteAsync();
            Assert.NotNull(modal.Error);
            await modal.CancelCommand.ExecuteAsync();
        };

        await sell.VoidCommand.ExecuteAsync();

        Assert.Equal(OrderStatuses.Draft, pos.Server.PeekOrder(sell.Order!.Id)!.Status);
    }

    [Fact]
    public async Task ReasonIsRequired()
    {
        var (pos, sell, _) = await SellWithLineAsync();
        using var _ = pos;
        SensitiveActionViewModel? modal = null;
        pos.Navigator.Script = async m =>
        {
            modal = (SensitiveActionViewModel)m;
            await modal.SubmitCommand.ExecuteAsync();
            Assert.Equal("A reason is required.", modal.Error);
            await modal.CancelCommand.ExecuteAsync();
        };

        await sell.VoidCommand.ExecuteAsync();

        Assert.DoesNotContain(pos.Server.Requests, r => r.Path.EndsWith("/void", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PriceOverride_NeedsApproval_AndTheValueIsValidated()
    {
        var (pos, sell, line) = await SellWithLineAsync();
        using var _ = pos;
        pos.Navigator.Script = async m =>
        {
            var modal = (SensitiveActionViewModel)m;
            modal.Reason = "Price match";
            modal.Value = "abc";
            await modal.SubmitCommand.ExecuteAsync();
            Assert.Contains("naira and kobo", modal.Error, StringComparison.Ordinal);
            modal.Value = "3000";
            var submit = modal.SubmitCommand.ExecuteAsync();
            await WaitUntilAsync(() => modal.IsWaiting);
            pos.Server.DecideApprovalAsSupervisor(pos.Server.PendingApprovalIds.Single(), true);
            await submit;
        };

        await sell.PriceOverrideCommand.ExecuteAsync(line);

        Assert.Equal("₦3,000.00", sell.TotalText);
    }

    // ApprovalCoordinator ------------------------------------------------------------------------------------------
    [Fact]
    public async Task Coordinator_PushHint_WakesThePollImmediately()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]), IdempotencyKeys.New());
        var pending = await pos.Env.Api.VoidOrderAsync(order.Id, order.RowVersion, new VoidRequest("x"), IdempotencyKeys.New());
        var approval = pending.Pending!.Approval;
        // A coordinator with a delay that never completes: only the push can wake it.
        var coordinator = new ApprovalCoordinator(pos.Env.Api, new() { PollIntervalSeconds = 3600, WaitTimeoutMinutes = 60 }, pos.Env.Time, (_, ct) => Task.Delay(Timeout.Infinite, ct));

        var wait = coordinator.WaitForDecisionAsync(approval);
        pos.Server.DecideApprovalAsSupervisor(approval.Id, true);
        for (var i = 0; i < 100 && !wait.IsCompleted; i++)
        {
            coordinator.NotifyDecided(approval.Id);
            await Task.Delay(5);
        }

        var result = await wait;
        Assert.True(result.Approved);
    }

    [Fact]
    public async Task Coordinator_ToleratesATransientOutageWhileWaiting()
    {
        using var pos = new TestPos();
        await pos.SignInAsync();
        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]), IdempotencyKeys.New());
        var approval = (await pos.Env.Api.VoidOrderAsync(order.Id, order.RowVersion, new VoidRequest("x"), IdempotencyKeys.New())).Pending!.Approval;
        var polls = 0;
        var coordinator = new ApprovalCoordinator(pos.Env.Api, new() { PollIntervalSeconds = 1, WaitTimeoutMinutes = 10 }, pos.Env.Time, (_, _) =>
        {
            polls++;
            pos.Server.Offline = polls < 3;          // unreachable for the first polls...
            if (polls == 3)
            {
                pos.Server.DecideApprovalAsSupervisor(approval.Id, true); // ...decided while we could not see it
            }

            pos.Env.Time.Advance(TimeSpan.FromSeconds(1));
            return Task.CompletedTask;
        });

        var result = await coordinator.WaitForDecisionAsync(approval);

        Assert.True(result.Approved);
        Assert.False(result.TimedOut);
    }
}
