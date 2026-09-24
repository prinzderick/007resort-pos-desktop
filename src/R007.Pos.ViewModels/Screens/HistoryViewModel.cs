using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

public sealed class PaymentRow(Payment payment, HistoryViewModel owner)
{
    public Payment Payment { get; } = payment;

    public string TimeText => Payment.CreatedAt.ToOffset(TimeSpan.FromHours(1)).ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public string Tender => Payment.TenderType;

    public string AmountText => MoneyFormat.Display(Payment.Amount, Payment.Currency);

    public string Status => Payment.Status;

    public string Reference => Payment.Reference ?? Payment.ProviderReference ?? string.Empty;

    public bool CanReprint => owner.CanReprint && Payment.ReceiptId is not null;

    public bool CanRefund => owner.CanRefund && Payment.Status is PaymentStatuses.Captured or PaymentStatuses.PartiallyRefunded;

    public AsyncRelayCommand ReprintCommand { get; } = new(() => owner.ReprintAsync(payment), null, owner.ReportError);

    public AsyncRelayCommand RefundCommand { get; } = new(() => owner.RefundAsync(payment), null, owner.ReportError);

    public AsyncRelayCommand ReverseCommand { get; } = new(() => owner.ReverseAsync(payment), null, owner.ReportError);
}

/// <summary>Transaction history (payments) with reprint, refund and reversal, each shown only when the staff member holds the permission.</summary>
public sealed class HistoryViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private readonly INavigator _nav;
    private string? _cursor;
    private bool _mySessionOnly;

    public HistoryViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        _nav = nav;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(() => LoadAsync(reset: true)), null, SetError);
        MoreCommand = new AsyncRelayCommand(() => RunAsync(() => LoadAsync(reset: false)), () => _cursor is not null, SetError);
    }

    public ObservableCollection<PaymentRow> Rows { get; } = [];

    public bool CanReprint => _ctx.Features.CanReprint;

    public bool CanRefund => _ctx.Features.CanRefund;

    public bool MySessionOnly
    {
        get => _mySessionOnly;
        set
        {
            if (SetProperty(ref _mySessionOnly, value))
            {
                _ = RunAsync(() => LoadAsync(reset: true));
            }
        }
    }

    public bool HasMore => _cursor is not null;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand MoreCommand { get; }

    public override Task ActivateAsync() => RunAsync(() => LoadAsync(reset: true));

    internal void ReportError(Exception ex) => SetError(ex);

    private async Task LoadAsync(bool reset)
    {
        var page = await _ctx.Api.ListPaymentsAsync(_ctx.FacilityId, MySessionOnly ? _ctx.CashSession?.Id : null, reset ? null : _cursor).ConfigureAwait(true);
        if (reset)
        {
            Rows.Clear();
        }

        foreach (var p in page.Items)
        {
            Rows.Add(new PaymentRow(p, this));
        }

        _cursor = page.NextCursor;
        OnPropertyChanged(nameof(HasMore));
        MoreCommand.RaiseCanExecuteChanged();
    }

    internal async Task ReprintAsync(Payment payment)
    {
        Error = null;
        if (payment.ReceiptId is not { } receiptId)
        {
            Error = "This payment has no receipt.";
            return;
        }

        Info = (await _ctx.Printing.PrintReceiptAsync(receiptId, reprint: true).ConfigureAwait(true)).Message;
    }

    internal async Task RefundAsync(Payment payment)
    {
        Error = null;
        var modal = new SensitiveActionViewModel(
            _ctx,
            "Refund payment",
            $"Refund part or all of {MoneyFormat.Display(payment.Amount, payment.Currency)} ({payment.TenderType}). Enter the amount to refund.",
            Permissions.RefundApprove,
            async input => SensitiveResult.From(await _ctx.Api.RefundPaymentAsync(payment.Id, new RefundRequest(MoneyFormat.ParseWire(input.Value), input.Reason), IdempotencyKeys.New(), input.StepUpToken).ConfigureAwait(true)),
            true,
            "payment",
            payment.Id,
            SensitiveValueKind.Amount)
        {
            Value = MoneyFormat.ToWire(payment.Amount - (payment.RefundedAmount ?? 0m)).TrimEnd('0').TrimEnd('.'),
        };
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await RunAsync(() => LoadAsync(reset: true)).ConfigureAwait(true);
    }

    internal async Task ReverseAsync(Payment payment)
    {
        Error = null;
        var modal = new SensitiveActionViewModel(
            _ctx,
            "Reverse payment",
            "Reverse this payment (same-session correction, e.g. wrong tender). The order becomes payable again.",
            Permissions.PaymentReversalApprove,
            async input => SensitiveResult.From(await _ctx.Api.ReversePaymentAsync(payment.Id, new ReversalRequest(input.Reason), IdempotencyKeys.New(), input.StepUpToken).ConfigureAwait(true)),
            true,
            "payment",
            payment.Id);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await RunAsync(() => LoadAsync(reset: true)).ConfigureAwait(true);
    }
}
