using System.Collections.ObjectModel;
using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.Core.Offline;
using R007.Pos.ViewModels.Infrastructure;
using R007.Pos.ViewModels.Services;

namespace R007.Pos.ViewModels.Screens;

/// <summary>What is being paid: one order, or a whole open tab (settle on exit).</summary>
public sealed record PaymentTarget(
    Guid FacilityId,
    Guid? OrderId,
    Guid? TabId,
    string Label,
    decimal AmountDue,
    string Currency,
    bool IsPendingConfirmation,
    bool AmountIsEstimate,
    bool FixedAmount,
    Booking? Booking = null)
{
    public static PaymentTarget ForOrder(WorkingOrder o) =>
        new(o.FacilityId, o.Id, null, $"Order {o.Number}", o.AmountDue, o.Currency, o.IsPendingConfirmation, o.BalanceDue is null, false);

    public static PaymentTarget ForTab(Tab t, string label) =>
        new(t.FacilityId, null, t.Id, label, t.BalanceDue, t.Currency ?? "NGN", false, false, true);

    /// <summary>
    /// A held booking whose paying order (slot fee + rentals) is attached: the order is paid through <c>POST /payments</c>; the node
    /// confirms the booking and issues the QR inside that payment. (<c>/bookings/{id}/confirm</c> returns neither receipt nor change.)
    /// </summary>
    public static PaymentTarget ForBooking(Booking b, Guid facilityId, decimal balanceDue) =>
        new(facilityId, b.OrderId, null, $"Booking {b.Number}", balanceDue, "NGN", false, false, true, b);
}

public sealed class TenderEntry(string method, decimal amount, decimal? tendered, string? reference) : ObservableObject
{
    public string Method { get; } = method;

    public decimal Amount { get; } = amount;

    public decimal? Tendered { get; } = tendered;

    public string? Reference { get; } = reference;

    public string Display => Method switch
    {
        TenderTypes.Cash => "Cash",
        TenderTypes.Card => "Card",
        TenderTypes.PosTerminal => "POS terminal",
        TenderTypes.Transfer => "Bank transfer",
        _ => Method,
    };

    public string AmountText => MoneyFormat.Display(Amount);

    public string Detail => Method == TenderTypes.Cash && Tendered is { } t ? $"tendered {MoneyFormat.Display(t)}" : Reference ?? string.Empty;
}

/// <summary>
/// Takes payment: cash (server-computed change), card / POS terminal / transfer as recorded tenders with a reference,
/// split across several tenders in one atomic request, partial settle of an order, or a whole tab. The same
/// <c>Idempotency-Key</c> and tender ids are reused for every retry of one attempt (double-tap or timeout can never charge
/// twice); editing the tenders starts a new attempt. If the API is unreachable, ONLY an all-cash payment can be queued
/// (encrypted emergency queue) and it is shown as "pending confirmation", never as paid.
/// </summary>
public sealed class PaymentViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private readonly PaymentTarget _target;
    private Attempt? _attempt;
    private string _amountToPayText;
    private string _method = TenderTypes.Cash;
    private string _tenderAmountText = string.Empty;
    private string _tenderedText = string.Empty;
    private string _reference = string.Empty;
    private string? _paystackEmail;
    private string? _paystackUrl;
    private string? _paystackReference;
    private string? _paystackStatus;

    public PaymentViewModel(PosContext ctx, PaymentTarget target)
    {
        _ctx = ctx;
        _target = target;
        _amountToPayText = MoneyFormat.ToWire(target.AmountDue).TrimEnd('0').TrimEnd('.');
        if (!_amountToPayText.Contains('.', StringComparison.Ordinal))
        {
            _amountToPayText += ".00";
        }

        _tenderAmountText = _amountToPayText;
        Methods = TenderTypes.All;
        AddTenderCommand = new RelayCommand(AddTender, () => !IsBusy);
        RemoveTenderCommand = new RelayCommand(p => RemoveTender(p as TenderEntry));
        PayCommand = new AsyncRelayCommand(PayAsync, () => CanPay, SetError);
        CancelCommand = new RelayCommand(() => Close(Paid));
        SelectMethodCommand = new RelayCommand(p => Method = p as string ?? TenderTypes.Cash);
        PaystackStartCommand = new AsyncRelayCommand(StartPaystackAsync, () => CanUsePaystack, SetError);
        PaystackVerifyCommand = new AsyncRelayCommand(VerifyPaystackAsync, () => _paystackReference is not null && !IsBusy, SetError);
    }

    public string Label => _target.Label;

    public string AmountDueText => (_target.AmountIsEstimate ? "Estimate " : string.Empty) + MoneyFormat.Display(_target.AmountDue, _target.Currency);

    public override string Title => "Take payment";

    public IReadOnlyList<string> Methods { get; }

    public ObservableCollection<TenderEntry> Tenders { get; } = [];

    public bool IsPendingConfirmationOrder => _target.IsPendingConfirmation;

    public bool AmountIsEstimate => _target.AmountIsEstimate;

    public bool CanEditAmount => !_target.FixedAmount;

    public bool CanSplit => _ctx.Features.CanSplit;

    /// <summary>The API's response for the completed attempt (receipt id, change).</summary>
    public PaymentResult? Result { get; private set; }

    public bool Paid { get; private set; }

    public bool QueuedPending { get; private set; }

    public string? ChangeText { get; private set; }

    public string? PrintMessage { get; private set; }

    public string AmountToPayText
    {
        get => _amountToPayText;
        set
        {
            if (SetProperty(ref _amountToPayText, value))
            {
                _attempt = null;
                RaiseTotals();
            }
        }
    }

    public string Method
    {
        get => _method;
        set
        {
            if (SetProperty(ref _method, value))
            {
                OnPropertyChanged(nameof(IsCash));
                OnPropertyChanged(nameof(NeedsReference));
            }
        }
    }

    public bool IsCash => _method == TenderTypes.Cash;

    public bool NeedsReference => TenderTypes.NeedsReference(_method);

    public string TenderAmountText
    {
        get => _tenderAmountText;
        set => SetProperty(ref _tenderAmountText, value);
    }

    public string TenderedText
    {
        get => _tenderedText;
        set => SetProperty(ref _tenderedText, value);
    }

    public string Reference
    {
        get => _reference;
        set => SetProperty(ref _reference, value);
    }

    public string? PaystackEmail
    {
        get => _paystackEmail;
        set => SetProperty(ref _paystackEmail, value);
    }

    public string? PaystackUrl
    {
        get => _paystackUrl;
        private set => SetProperty(ref _paystackUrl, value);
    }

    public string? PaystackReference
    {
        get => _paystackReference;
        private set => SetProperty(ref _paystackReference, value);
    }

    public string? PaystackStatus
    {
        get => _paystackStatus;
        private set => SetProperty(ref _paystackStatus, value);
    }

    public bool CanUsePaystack => !IsBusy && !_target.FixedAmount && _target.OrderId is not null && !_target.IsPendingConfirmation;

    /// <summary>Sum of the tenders entered so far (sum of what staff typed, not a price computation).</summary>
    public decimal TenderedTotal => Tenders.Sum(t => t.Amount);

    public string TenderedTotalText => MoneyFormat.Display(TenderedTotal, _target.Currency);

    public string RemainingText => TryAmountToPay(out var due) ? MoneyFormat.Display(due - TenderedTotal, _target.Currency) : string.Empty;

    public bool CanPay => !IsBusy && !Paid && Tenders.Count > 0 && TryAmountToPay(out var due) && due == TenderedTotal;

    public RelayCommand AddTenderCommand { get; }

    public RelayCommand RemoveTenderCommand { get; }

    public RelayCommand SelectMethodCommand { get; }

    public AsyncRelayCommand PayCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand PaystackStartCommand { get; }

    public AsyncRelayCommand PaystackVerifyCommand { get; }

    private bool TryAmountToPay(out decimal amount) => MoneyFormat.TryParseEntered(_amountToPayText, out amount);

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(TenderedTotal));
        OnPropertyChanged(nameof(TenderedTotalText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(CanPay));
        PayCommand.RaiseCanExecuteChanged();
    }

    private void AddTender()
    {
        Error = null;
        if (!TryAmountToPay(out var due))
        {
            Error = "Enter the amount to pay now (naira and kobo, e.g. 2500.00).";
            return;
        }

        if (!MoneyFormat.TryParseEntered(TenderAmountText, out var amount))
        {
            Error = "Enter this tender's amount (naira and kobo, e.g. 2500.00).";
            return;
        }

        if (Tenders.Count > 0 && !CanSplit)
        {
            Error = "You do not have permission to split a payment across several tenders.";
            return;
        }

        if (TenderedTotal + amount > due)
        {
            Error = "That is more than the amount to pay.";
            return;
        }

        decimal? tendered = null;
        if (IsCash)
        {
            if (string.IsNullOrWhiteSpace(TenderedText))
            {
                tendered = amount;
            }
            else if (!MoneyFormat.TryParseEntered(TenderedText, out var t) || t < amount)
            {
                Error = "Cash handed over must be at least the tender amount.";
                return;
            }
            else
            {
                tendered = t;
            }
        }

        string? reference = null;
        if (NeedsReference)
        {
            if (string.IsNullOrWhiteSpace(Reference))
            {
                Error = "Enter the terminal / transfer reference.";
                return;
            }

            reference = Reference.Trim();
        }

        if (IsCash && _ctx.Features.RequireCashSession && !_ctx.HasOpenCashSession)
        {
            Error = "Open a cash session before taking cash.";
            return;
        }

        Tenders.Add(new TenderEntry(Method, amount, tendered, reference));
        _attempt = null;
        TenderedText = string.Empty;
        Reference = string.Empty;
        TenderAmountText = MoneyFormat.ToWire(Math.Max(0m, due - TenderedTotal)).TrimEnd('0').TrimEnd('.');
        RaiseTotals();
    }

    private void RemoveTender(TenderEntry? entry)
    {
        if (entry is not null && Tenders.Remove(entry))
        {
            _attempt = null;
            RaiseTotals();
        }
    }

    private CreatePaymentRequest BuildRequest(Attempt attempt, decimal amountToPay)
    {
        var tenders = Tenders.Select((t, i) => new TenderInput(t.Method, t.Amount, t.Reference, t.Tendered, attempt.TenderIds[i], _ctx.Time.GetUtcNow())).ToList();
        return new CreatePaymentRequest(
            _target.FacilityId,
            _target.OrderId is { } orderId ? [new AllocationInput(orderId, amountToPay)] : [],
            tenders,
            _ctx.CashSession?.Id,
            _target.TabId,
            null,
            _ctx.Time.GetUtcNow());
    }

    private async Task PayAsync()
    {
        Error = null;
        if (!TryAmountToPay(out var amountToPay) || amountToPay != TenderedTotal)
        {
            Error = "The tenders must add up to the amount to pay.";
            return;
        }

        if (Tenders.Any(t => t.Method == TenderTypes.Cash) && _ctx.Features.RequireCashSession && !_ctx.HasOpenCashSession)
        {
            Error = "Open a cash session before taking cash.";
            return;
        }

        var attempt = _attempt ??= new Attempt(IdempotencyKeys.New(), [.. Tenders.Select(_ => ClientIds.New())]);
        IsBusy = true;
        PayCommand.RaiseCanExecuteChanged();
        try
        {
            var request = BuildRequest(attempt, amountToPay);
            PaymentResult result;
            try
            {
                result = _target.TabId is { } tabId
                    ? await _ctx.Api.SettleTabAsync(tabId, new SettleTabRequest(request.Tenders, request.CashSessionId), attempt.Key).ConfigureAwait(true)
                    : await _ctx.Api.CreatePaymentAsync(request, attempt.Key).ConfigureAwait(true);
            }
            catch (ApiUnavailableException) when (_target.OrderId is not null && _target.Booking is null && Tenders.All(t => t.Method == TenderTypes.Cash))
            {
                await QueueCashAsync(request, attempt.Key).ConfigureAwait(true);
                return;
            }
            catch (ApiUnavailableException)
            {
                Error = "The server is unreachable. Only cash can be recorded during an outage; card, terminal and transfer payments must be confirmed live. Try again when the connection returns.";
                return;
            }

            Result = result;
            Paid = true;
            ChangeText = result.ChangeDue is { } change and > 0m ? "Change due: " + MoneyFormat.Display(change) : null;
            if (_target.Booking is { } booking)
            {
                await FinishBookingAsync(booking, result).ConfigureAwait(true);
            }
            else
            {
                // Tickets/rentals: the receipt is printed once, WITH the QR (or followed by one slip per ticket). Otherwise a plain receipt.
                if (!await IssueTicketsIfNeededAsync().ConfigureAwait(true))
                {
                    PrintMessage = (await _ctx.Printing.PrintReceiptAsync(result.ReceiptId).ConfigureAwait(true)).Message;
                }
            }

            OnPropertyChanged(nameof(Paid));
            OnPropertyChanged(nameof(ChangeText));
            OnPropertyChanged(nameof(PrintMessage));
            OnPropertyChanged(nameof(Result));
            Info = "Payment recorded.";
        }
        catch (ApiException ex)
        {
            Error = Describe(ex);
            if (ex.Code is "balance_changed" or "order_state_invalid" or "concurrency_conflict")
            {
                _attempt = null; // the world changed: a fresh attempt (new key) is required after the operator re-checks
            }
        }
        finally
        {
            IsBusy = false;
            PayCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanPay));
        }
    }

    /// <summary>
    /// The booking order was paid: the node has confirmed the booking and issued its QR entitlement (slot access + rentals). Print the
    /// receipt with that QR. Booking payment is never queued offline: a hold is scarce and must be confirmed live.
    /// </summary>
    private async Task FinishBookingAsync(Booking held, PaymentResult result)
    {
        var booking = await _ctx.Api.GetBookingAsync(held.Id).ConfigureAwait(true);
        ConfirmedBooking = booking;
        if (booking.EntitlementId is not { } entitlementId)
        {
            PrintMessage = (await _ctx.Printing.PrintReceiptAsync(result.ReceiptId).ConfigureAwait(true)).Message + " Booking not confirmed yet: check the booking.";
            return;
        }

        var entitlement = await _ctx.Api.GetEntitlementAsync(entitlementId).ConfigureAwait(true);
        EntitlementQr = entitlement.QrToken;
        var printed = await _ctx.Printing.PrintReceiptAsync(result.ReceiptId, false, entitlement.QrToken).ConfigureAwait(true);
        PrintMessage = "QR entitlement receipt: " + printed.Message;
        OnPropertyChanged(nameof(EntitlementQr));
    }

    /// <summary>One slip per QR ticket (an order of 5 individual pool tickets has 5 entitlements, each scanned separately).</summary>
    internal static R007.Pos.Devices.Printing.ReceiptDocument TicketSlip(Entitlement e, string? facilityName) =>
        new(
            [
                new R007.Pos.Devices.Printing.ReceiptLine("007 Resort & Spa", R007.Pos.Devices.Printing.ReceiptAlignment.Center, true),
                .. e.Items.Select(i => new R007.Pos.Devices.Printing.ReceiptLine($"{i.Quantity} x {i.Name}", R007.Pos.Devices.Printing.ReceiptAlignment.Center)),
                new R007.Pos.Devices.Printing.ReceiptLine("Scan at the gate. One use per ticket.", R007.Pos.Devices.Printing.ReceiptAlignment.Center),
            ],
            true,
            e.QrToken);

    public Booking? ConfirmedBooking { get; private set; }

    public string? EntitlementQr { get; private set; }

    /// <summary>Every QR entitlement issued for this payment (one per individual ticket).</summary>
    public IReadOnlyList<Entitlement> Entitlements { get; private set; } = [];

    private async Task QueueCashAsync(CreatePaymentRequest request, string key)
    {
        var body = JsonSerializer.Serialize(request, PosJsonContext.Default.CreatePaymentRequest);
        var queued = await _ctx.Emergency.QueueAsync("api/v1/payments", body, key, $"Cash {MoneyFormat.Display(TenderedTotal)} for {_target.Label}", $"api/v1/orders/{_target.OrderId:D}", false).ConfigureAwait(true);
        if (queued.IsQueued)
        {
            QueuedPending = true;
            Paid = false;
            Info = "CASH SAVED ON THIS TERMINAL - NOT CONFIRMED. It will be sent to the server when the connection returns. Give the customer a handwritten acknowledgement, not a receipt.";
            OnPropertyChanged(nameof(QueuedPending));
            Close(true);
        }
        else
        {
            Error = queued.Message;
        }
    }

    /// <summary>
    /// Reception: paying for tickets/rentals yields QR entitlements. The node issues them itself once the order is paid in full
    /// (a PAY_FIRST order stays DRAFT, so "paid" means balance 0, not SETTLED); <c>POST /entitlements</c> is the idempotent fallback.
    /// One QR per individual ticket: a single QR rides on the receipt, several print as separate slips.
    /// </summary>
    private async Task<bool> IssueTicketsIfNeededAsync()
    {
        if (_target.OrderId is not { } orderId || !_ctx.Features.CanIssueTickets || Result is null)
        {
            return false;
        }

        try
        {
            var order = await _ctx.Api.GetOrderAsync(orderId).ConfigureAwait(true);
            var sellsEntitlements = order.BalanceDue <= 0m
                && order.AmountPaid > 0m
                && order.Lines.Any(l => _ctx.Products.Any(p => p.Id == l.ProductId && p.Kind is ProductKinds.Ticket or ProductKinds.Rental));
            if (!sellsEntitlements)
            {
                return false;
            }

            var entitlements = await _ctx.Api.ListEntitlementsAsync(orderId).ConfigureAwait(true);
            if (entitlements.Count == 0)
            {
                await _ctx.Api.IssueEntitlementAsync(new IssueEntitlementRequest(orderId), IdempotencyKeys.New()).ConfigureAwait(true);
                entitlements = await _ctx.Api.ListEntitlementsAsync(orderId).ConfigureAwait(true);
            }

            if (entitlements.Count == 0)
            {
                return false;
            }

            Entitlements = entitlements;
            EntitlementQr = entitlements[0].QrToken;
            var printed = await _ctx.Printing.PrintReceiptAsync(Result.ReceiptId, false, entitlements.Count == 1 ? entitlements[0].QrToken : null).ConfigureAwait(true);
            var slips = 0;
            if (entitlements.Count > 1)
            {
                foreach (var e in entitlements)
                {
                    slips += (await _ctx.Printing.PrintAsync(TicketSlip(e, _ctx.FacilityName)).ConfigureAwait(true)).Printed ? 1 : 0;
                }
            }

            PrintMessage = entitlements.Count == 1
                ? "QR entitlement receipt: " + printed.Message
                : $"Receipt: {printed.Message} {slips} of {entitlements.Count} ticket QR slips printed.";
            OnPropertyChanged(nameof(EntitlementQr));
            OnPropertyChanged(nameof(Entitlements));
            return true;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            PrintMessage = "Payment taken, but the ticket QR could not be issued: " + Describe(ex) + " Reissue it from History.";
            return false;
        }
    }

    // Paystack pay-link (placeholder flow: initialise -> customer pays on the link -> verify by reference) ----
    private async Task StartPaystackAsync()
    {
        if (string.IsNullOrWhiteSpace(PaystackEmail) || !PaystackEmail.Contains('@', StringComparison.Ordinal))
        {
            Error = "Enter the customer's email for the pay-link.";
            return;
        }

        if (!TryAmountToPay(out var amount) || _target.OrderId is not { } orderId)
        {
            Error = "Enter the amount to pay now first.";
            return;
        }

        await RunAsync(async () =>
        {
            var init = await _ctx.Api.PaystackInitializeAsync(new PaystackInitRequest(amount, PaystackEmail.Trim(), [orderId]), IdempotencyKeys.New()).ConfigureAwait(true);
            PaystackUrl = init.AuthorizationUrl;
            PaystackReference = init.Reference;
            PaystackStatus = "Waiting for the customer to pay on the link. Then press Check payment.";
            PaystackVerifyCommand.RaiseCanExecuteChanged();
        }).ConfigureAwait(true);
    }

    private async Task VerifyPaystackAsync()
    {
        await RunAsync(async () =>
        {
            var payment = await _ctx.Api.PaystackVerifyAsync(_paystackReference!).ConfigureAwait(true);
            if (payment.IsCaptured)
            {
                PaystackStatus = "Paid. Payment captured by Paystack.";
                Paid = true;
                Result = new PaymentResult([payment], null, payment.ReceiptId ?? Guid.Empty, null);
                if (payment.ReceiptId is { } receiptId)
                {
                    PrintMessage = (await _ctx.Printing.PrintReceiptAsync(receiptId).ConfigureAwait(true)).Message;
                }

                OnPropertyChanged(nameof(Paid));
                OnPropertyChanged(nameof(Result));
                OnPropertyChanged(nameof(PrintMessage));
            }
            else if (payment.Status is PaymentStatuses.Failed or PaymentStatuses.Cancelled)
            {
                PaystackStatus = "The payment failed or was cancelled. Start a new pay-link or use another method.";
            }
            else
            {
                PaystackStatus = "Not paid yet. Ask the customer to complete payment, then check again.";
            }
        }).ConfigureAwait(true);
    }

    private sealed record Attempt(string Key, IReadOnlyList<Guid> TenderIds);
}
