using R007.Pos.Core.Api;
using R007.Pos.ViewModels.Screens;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.Tests;

/// <summary>Drives a PaymentViewModel the way the payment dialog does: pick a method, add a tender for the full amount, press Pay.</summary>
public sealed class PaymentViewModelHarness
{
    public PaymentViewModelHarness(TestPos pos, WorkingOrder order)
    {
        Modal = new PaymentViewModel(pos.Ctx, PaymentTarget.ForOrder(order));
    }

    public PaymentViewModel Modal { get; }

    public async Task PayWithAsync(string method, string? reference, string? tendered = null)
    {
        Modal.Method = method;
        Modal.Reference = reference ?? string.Empty;
        Modal.TenderedText = tendered ?? string.Empty;
        Modal.AddTenderCommand.Execute(null);
        Assert.Null(Modal.Error);
        await Modal.PayCommand.ExecuteAsync();
    }
}
