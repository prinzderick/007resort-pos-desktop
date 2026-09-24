using System.Collections.ObjectModel;
using System.Globalization;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

public enum AgeLevel
{
    Normal,
    Warning,
    Critical,
    Expired,
}

/// <summary>How long a waiter-collected payment has waited, and how close it is to auto-expiry (the node expires it after the facility's window).</summary>
public static class CollectionAge
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);

    public static AgeLevel Level(DateTimeOffset createdAt, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var window = expiresAt is { } e && e > createdAt ? e - createdAt : DefaultWindow;
        var deadline = createdAt + window;
        var remaining = deadline - now;
        if (remaining <= TimeSpan.Zero)
        {
            return AgeLevel.Expired;
        }

        var critical = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(2).Ticks, window.Ticks / 5));
        if (remaining <= critical)
        {
            return AgeLevel.Critical;
        }

        return now - createdAt >= TimeSpan.FromTicks(window.Ticks / 2) ? AgeLevel.Warning : AgeLevel.Normal;
    }

    public static string Text(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var age = now - createdAt;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        return age.TotalMinutes < 60
            ? $"{(int)age.TotalMinutes} min"
            : string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h {age.Minutes:00} min");
    }
}

/// <summary>One waiter-collected payment waiting for the cashier.</summary>
public sealed class CollectionRow : ObservableObject
{
    private DateTimeOffset _now;

    public CollectionRow(Payment payment, CollectionsInboxViewModel owner, DateTimeOffset now, string? orderNumber, string? tableLabel, string? waiterName)
    {
        Payment = payment;
        _now = now;
        OrderNumber = orderNumber ?? payment.Collection?.OrderNumber;
        TableLabel = tableLabel ?? payment.Collection?.TableLabel;
        WaiterName = waiterName ?? payment.Collection?.CollectedByName;
        ConfirmCommand = new AsyncRelayCommand(() => owner.ConfirmAsync(this), () => owner.CanConfirm, owner.ReportError);
        RejectCommand = new AsyncRelayCommand(() => owner.RejectAsync(this), () => owner.CanConfirm, owner.ReportError);
    }

    public Payment Payment { get; }

    public CollectionInfo? Info => Payment.Collection;

    public Guid PaymentId => Payment.Id;

    public string? OrderNumber { get; }

    public string? TableLabel { get; }

    public string? WaiterName { get; }

    public string TableText => string.IsNullOrWhiteSpace(TableLabel) ? "No table" : $"Table {TableLabel}";

    public string OrderText => string.IsNullOrWhiteSpace(OrderNumber) ? "Order" : $"Order {OrderNumber}";

    public string WaiterText => !string.IsNullOrWhiteSpace(WaiterName)
        ? WaiterName!
        : Info?.CollectedByStaffId is { } id ? "Waiter " + id.ToString("N")[..6] : "Waiter";

    public string TenderText => CollectionTenders.Label(Info?.Tender);

    public string AmountText => MoneyFormat.Display(Payment.Amount, Payment.Currency);

    /// <summary>Approval code / slip / bank reference / last 4 digits exactly as the waiter recorded them.</summary>
    public string ReferenceText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Info?.ApprovalCode))
            {
                parts.Add("Approval " + Info!.ApprovalCode);
            }

            if (!string.IsNullOrWhiteSpace(Info?.SlipReference))
            {
                parts.Add("Slip " + Info!.SlipReference);
            }

            if (!string.IsNullOrWhiteSpace(Info?.BankReference))
            {
                parts.Add("Bank ref " + Info!.BankReference);
            }

            if (!string.IsNullOrWhiteSpace(Info?.Last4))
            {
                parts.Add("Card **** " + Info!.Last4);
            }

            if (Info?.Tender == CollectionTenders.Cash && Payment.Tendered is { } t && t != Payment.Amount)
            {
                parts.Add("Handed over " + MoneyFormat.Display(t, Payment.Currency));
            }

            return parts.Count == 0 ? "No reference recorded" : string.Join("  |  ", parts);
        }
    }

    public string TerminalText => !string.IsNullOrWhiteSpace(Info?.TerminalLabel)
        ? "Terminal " + Info!.TerminalLabel
        : Info?.TerminalId is { } id ? "Terminal " + id.ToString("N")[..6] : string.Empty;

    public bool HasTerminal => TerminalText.Length > 0;

    public AgeLevel Level => CollectionAge.Level(Payment.CreatedAt, Info?.ExpiresAt, _now);

    /// <summary>Name for XAML triggers: Normal / Warning / Critical / Expired.</summary>
    public string LevelName => Level.ToString();

    public string AgeText => CollectionAge.Text(Payment.CreatedAt, _now) + (Level == AgeLevel.Expired ? " (expiring)" : string.Empty);

    public AsyncRelayCommand ConfirmCommand { get; }

    public AsyncRelayCommand RejectCommand { get; }

    public void Tick(DateTimeOffset now)
    {
        _now = now;
        OnPropertyChanged(nameof(AgeText));
        OnPropertyChanged(nameof(Level));
        OnPropertyChanged(nameof(LevelName));
    }
}

/// <summary>
/// 'Collected by waiters': every payment a waiter took at a table (card-machine slip, cash, transfer) that is still PENDING_CONFIRMATION at this
/// facility. Money here is NOT yet the till's money: the cashier confirms it against the slip / bank alert / cash count, or rejects it (which alerts
/// a supervisor). The list is refreshed by realtime hints and by polling; the API is the only source of truth.
/// </summary>
public sealed class CollectionsInboxViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private readonly INavigator _nav;
    private string? _alert;
    private Guid? _lastReceiptId;

    public CollectionsInboxViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        _nav = nav;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync), null, SetError);
        ReprintReceiptCommand = new AsyncRelayCommand(ReprintAsync, () => _lastReceiptId is not null, SetError);
        DismissAlertCommand = new RelayCommand(() => Alert = null);
    }

    public ObservableCollection<CollectionRow> Items { get; } = [];

    public int PendingCount => Items.Count;

    public bool IsEmpty => Items.Count == 0;

    public string PendingAmountText => MoneyFormat.Display(Items.Sum(i => i.Payment.Amount));

    /// <summary>Only a holder of <c>payment.confirm</c> may decide (the API enforces it too).</summary>
    public bool CanConfirm => _ctx.Features.CanConfirmCollections;

    public string? Alert
    {
        get => _alert;
        private set
        {
            if (SetProperty(ref _alert, value))
            {
                OnPropertyChanged(nameof(HasAlert));
            }
        }
    }

    public bool HasAlert => !string.IsNullOrEmpty(_alert);

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ReprintReceiptCommand { get; }

    public RelayCommand DismissAlertCommand { get; }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    internal void ReportError(Exception ex) => SetError(ex);

    /// <summary>Quiet refresh for the shell badge, polling and realtime hints (network trouble keeps the last list).</summary>
    public async Task RefreshQuietlyAsync()
    {
        if (!CanConfirm)
        {
            return;
        }

        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // keep the last list
        }
    }

    /// <summary>Realtime hints (<c>payment.*</c>, <c>bill.printed</c>): reload over REST, and surface supervisor alerts.</summary>
    public async Task OnRealtimeAsync(string name, string? message)
    {
        if (name == "payment.alert" && !string.IsNullOrWhiteSpace(message))
        {
            Alert = message;
        }

        await RefreshQuietlyAsync().ConfigureAwait(true);
    }

    /// <summary>Moves the age clocks (call from the shell's background tick).</summary>
    public void TickAges()
    {
        var now = _ctx.Time.GetUtcNow();
        foreach (var row in Items)
        {
            row.Tick(now);
        }
    }

    private async Task LoadAsync()
    {
        var payments = await _ctx.Api.ListPaymentsByStatusAsync(_ctx.FacilityId, PaymentStatuses.PendingConfirmation).ConfigureAwait(true);
        var pending = payments.Where(p => p.Collection?.IsAutoConfirm != true).OrderBy(p => p.CreatedAt).ToList();

        var orders = new Dictionary<Guid, Order?>();
        IReadOnlyList<DiningTable>? tables = null;
        var now = _ctx.Time.GetUtcNow();
        var rows = new List<CollectionRow>();
        foreach (var p in pending)
        {
            var orderId = p.Collection?.OrderId ?? p.Allocations?.FirstOrDefault()?.OrderId;
            string? number = p.Collection?.OrderNumber;
            string? table = p.Collection?.TableLabel;
            if (orderId is { } id && (number is null || table is null))
            {
                if (!orders.TryGetValue(id, out var order))
                {
                    order = await TryGetOrderAsync(id).ConfigureAwait(true);
                    orders[id] = order;
                }

                number ??= order?.Number;
                if (table is null && order?.TableId is { } tableId)
                {
                    tables ??= await TryGetTablesAsync().ConfigureAwait(true);
                    table = tables.FirstOrDefault(t => t.Id == tableId)?.Label;
                }
            }

            rows.Add(new CollectionRow(p, this, now, number, table, null));
        }

        Items.Clear();
        foreach (var r in rows)
        {
            Items.Add(r);
        }

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PendingAmountText));
    }

    private async Task<Order?> TryGetOrderAsync(Guid id)
    {
        try
        {
            return await _ctx.Api.GetOrderAsync(id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<DiningTable>> TryGetTablesAsync()
    {
        try
        {
            return await _ctx.Api.GetTablesAsync(_ctx.FacilityId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return [];
        }
    }

    internal async Task ConfirmAsync(CollectionRow row)
    {
        Error = null;
        var modal = new ConfirmCollectionViewModel(_ctx, row);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await AfterDecisionAsync(modal.NeedsRefresh || modal.Confirmed).ConfigureAwait(true);
        if (modal.Confirmed)
        {
            _lastReceiptId = modal.ReceiptId;
            ReprintReceiptCommand.RaiseCanExecuteChanged();
            Info = $"Confirmed {row.AmountText} for {row.TableText}. Order settled. {modal.PrintMessage}".Trim();
        }
    }

    internal async Task RejectAsync(CollectionRow row)
    {
        Error = null;
        var modal = new RejectCollectionViewModel(_ctx, row);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await AfterDecisionAsync(modal.NeedsRefresh || modal.Rejected).ConfigureAwait(true);
        if (modal.Rejected)
        {
            Info = $"Rejected {row.AmountText} for {row.TableText}. A supervisor has been alerted.";
        }
    }

    private async Task AfterDecisionAsync(bool reload)
    {
        if (reload)
        {
            await RefreshQuietlyAsync().ConfigureAwait(true);
        }
    }

    private async Task ReprintAsync()
    {
        if (_lastReceiptId is { } id)
        {
            var result = await _ctx.Printing.PrintReceiptAsync(id, true).ConfigureAwait(true);
            Info = result.Message;
        }
    }
}

/// <summary>Confirm a collection: shows exactly what the waiter recorded, optionally checks the slip/alert reference the cashier reads out.</summary>
public sealed class ConfirmCollectionViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private readonly CollectionRow _row;
    private string _key = IdempotencyKeys.New();
    private string _referenceInput = string.Empty;
    private string _note = string.Empty;
    private bool _mismatchAcknowledged;
    private string? _warning;

    public ConfirmCollectionViewModel(PosContext ctx, CollectionRow row)
    {
        _ctx = ctx;
        _row = row;
        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => !IsBusy && !Confirmed && _ctx.Features.CanConfirmCollections, SetError);
        CancelCommand = new RelayCommand(() => Close(Confirmed));
    }

    public override string Title => "Confirm collected payment";

    public string TableText => _row.TableText;

    public string OrderText => _row.OrderText;

    public string WaiterText => _row.WaiterText;

    public string TenderText => _row.TenderText;

    public string AmountText => _row.AmountText;

    public string ReferenceText => _row.ReferenceText;

    public bool IsCash => _row.Info?.Tender == CollectionTenders.Cash;

    public string Instruction => IsCash
        ? "Count the cash the waiter handed over, then confirm."
        : _row.Info?.Tender == CollectionTenders.Transfer
            ? "Check the bank alert shows this amount and reference, then confirm."
            : "Check the slip / card machine approval matches, then confirm.";

    /// <summary>Optional: type the approval code / slip / bank reference you see; a mismatch asks for a second confirmation.</summary>
    public string ReferenceInput
    {
        get => _referenceInput;
        set
        {
            if (SetProperty(ref _referenceInput, value))
            {
                _mismatchAcknowledged = false;
                Warning = null;
                _key = IdempotencyKeys.New();
                OnPropertyChanged(nameof(ReferenceMismatch));
            }
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value))
            {
                _key = IdempotencyKeys.New();
            }
        }
    }

    /// <summary>True when a reference was typed and it is none of the references the waiter recorded.</summary>
    public bool ReferenceMismatch
    {
        get
        {
            var typed = ReferenceInput.Trim();
            if (typed.Length == 0)
            {
                return false;
            }

            var i = _row.Info;
            var known = new[] { i?.ApprovalCode, i?.SlipReference, i?.BankReference, i?.Last4, _row.Payment.Reference }
                .Where(r => !string.IsNullOrWhiteSpace(r));
            return !known.Any(r => string.Equals(r!.Trim(), typed, StringComparison.OrdinalIgnoreCase));
        }
    }

    public string? Warning
    {
        get => _warning;
        private set
        {
            if (SetProperty(ref _warning, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(_warning);

    public bool Confirmed { get; private set; }

    /// <summary>The list changed under the cashier (already decided / balance changed): reload it.</summary>
    public bool NeedsRefresh { get; private set; }

    public Guid? ReceiptId { get; private set; }

    public string? PrintMessage { get; private set; }

    public ConfirmCollectionResult? Result { get; private set; }

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task ConfirmAsync()
    {
        Error = null;
        if (ReferenceMismatch && !_mismatchAcknowledged)
        {
            _mismatchAcknowledged = true;
            Warning = "The reference you typed does not match what the waiter recorded. Check the slip again, or press Confirm once more to confirm anyway.";
            return;
        }

        if (IsCash && _ctx.Features.RequireCashSession && !_ctx.HasOpenCashSession)
        {
            Error = "Open a cash session before confirming cash.";
            return;
        }

        IsBusy = true;
        ConfirmCommand.RaiseCanExecuteChanged();
        try
        {
            var typed = ReferenceInput.Trim();
            Result = await _ctx.Api.ConfirmCollectionAsync(
                _row.PaymentId,
                new ConfirmCollectionRequest(typed.Length == 0 ? null : typed, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()),
                _key).ConfigureAwait(true);
            Confirmed = true;
            ReceiptId = Result.ReceiptId ?? Result.Payment.ReceiptId;
            PrintMessage = ReceiptId is { } receipt
                ? (await _ctx.Printing.PrintReceiptAsync(receipt).ConfigureAwait(true)).Message + " (Reprint it from the list if needed.)"
                : "No receipt was issued.";
            OnPropertyChanged(nameof(Confirmed));
            Close(true);
        }
        catch (ApiException ex)
        {
            Error = Describe(ex);
            NeedsRefresh = ex.Code is "payment_state_invalid" or "balance_changed" or "auto_confirm_only" or "not_found";
            if (ex.Code is "payment_state_invalid" or "auto_confirm_only" or "not_found")
            {
                Close(false); // nothing left to confirm; the inbox reloads and shows the message
            }
        }
        catch (ApiUnavailableException ex)
        {
            Error = Describe(ex) + " Nothing was confirmed; press Confirm again (it is safe to retry).";
        }
        finally
        {
            IsBusy = false;
            ConfirmCommand.RaiseCanExecuteChanged();
        }
    }
}

/// <summary>Reject a collection: a reason is mandatory and the cashier is told that a supervisor is alerted.</summary>
public sealed class RejectCollectionViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private readonly CollectionRow _row;
    private string _reason = string.Empty;
    private string _key = IdempotencyKeys.New();

    public RejectCollectionViewModel(PosContext ctx, CollectionRow row)
    {
        _ctx = ctx;
        _row = row;
        RejectCommand = new AsyncRelayCommand(RejectAsync, () => !IsBusy && !Rejected && _ctx.Features.CanConfirmCollections, SetError);
        CancelCommand = new RelayCommand(() => Close(Rejected));
    }

    public override string Title => "Reject collected payment";

    public string Summary => $"{_row.AmountText} by {_row.TenderText} for {_row.TableText} ({_row.WaiterText})";

    public string WarningText => "Rejecting is recorded and alerts a supervisor immediately. Use it when the slip, bank alert or cash does not match. The bill stays unpaid.";

    public string Reason
    {
        get => _reason;
        set
        {
            if (SetProperty(ref _reason, value))
            {
                _key = IdempotencyKeys.New();
            }
        }
    }

    public bool Rejected { get; private set; }

    public bool NeedsRefresh { get; private set; }

    public AsyncRelayCommand RejectCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task RejectAsync()
    {
        Error = null;
        if (Reason.Trim().Length < 3)
        {
            Error = "A reason is required (at least 3 characters).";
            return;
        }

        IsBusy = true;
        RejectCommand.RaiseCanExecuteChanged();
        try
        {
            await _ctx.Api.RejectCollectionAsync(_row.PaymentId, new RejectCollectionRequest(Reason.Trim()), _key).ConfigureAwait(true);
            Rejected = true;
            OnPropertyChanged(nameof(Rejected));
            Close(true);
        }
        catch (ApiException ex)
        {
            Error = Describe(ex);
            NeedsRefresh = ex.Code is "payment_state_invalid" or "not_found";
            if (NeedsRefresh)
            {
                Close(false);
            }
        }
        catch (ApiUnavailableException ex)
        {
            Error = Describe(ex) + " Nothing was rejected; press Reject again (it is safe to retry).";
        }
        finally
        {
            IsBusy = false;
            RejectCommand.RaiseCanExecuteChanged();
        }
    }
}
