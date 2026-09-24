using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>A waiter's cash handover waiting for the cashier to count it (or for a supervisor to sign off its variance).</summary>
public sealed class HandoverRow
{
    public HandoverRow(CashHandover handover, CashHandoverDeskViewModel owner, DateTimeOffset now)
    {
        Handover = handover;
        AgeText = CollectionAge.Text(handover.CreatedAt, now);
        ReceiveCommand = new AsyncRelayCommand(() => owner.ReceiveAsync(this), () => Handover.IsPendingReceipt && owner.CanReceive, owner.ReportError);
        SignoffCommand = new AsyncRelayCommand(() => owner.SignoffAsync(this), () => Handover.IsPendingSignoff && owner.CanSignoff, owner.ReportError);
    }

    public CashHandover Handover { get; }

    public string WaiterText => !string.IsNullOrWhiteSpace(Handover.WaiterName) ? Handover.WaiterName! : "Waiter " + Handover.WaiterStaffId.ToString("N")[..6];

    public string DeclaredText => "Declared " + MoneyFormat.Display(Handover.DeclaredAmount);

    public string StatusText => Handover.Status switch
    {
        HandoverStatuses.PendingReceipt => "Waiting to be counted",
        HandoverStatuses.PendingSignoff => "Variance needs supervisor sign-off",
        HandoverStatuses.Received => "Received",
        HandoverStatuses.SignedOff => "Signed off",
        _ => Handover.Status,
    };

    public string VarianceText => Handover.Variance is { } v
        ? v == 0m ? "Exact" : v < 0m ? "SHORT " + MoneyFormat.Display(-v) : "OVER " + MoneyFormat.Display(v)
        : string.Empty;

    public bool HasVariance => Handover.Variance is not null;

    public bool NeedsSignoff => Handover.IsPendingSignoff;

    public string AgeText { get; }

    public AsyncRelayCommand ReceiveCommand { get; }

    public AsyncRelayCommand SignoffCommand { get; }
}

/// <summary>A waiter currently holding cash (from the API's cash-in-hand position).</summary>
public sealed class HoldingRow(CashInHand position)
{
    public CashInHand Position { get; } = position;

    public string WaiterText => !string.IsNullOrWhiteSpace(Position.WaiterName) ? Position.WaiterName! : "Waiter " + Position.StaffId.ToString("N")[..6];

    public string AmountText => MoneyFormat.Display(Position.CashInHandAmount);

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Position.Limit is { } limit)
            {
                parts.Add("limit " + MoneyFormat.Display(limit));
            }

            if (Position.HandoverRequired == true)
            {
                parts.Add("HANDOVER DUE");
            }

            if (Position.PendingCollections is > 0)
            {
                parts.Add($"{Position.PendingCollections} awaiting confirmation");
            }

            return string.Join("  |  ", parts);
        }
    }
}

/// <summary>
/// Cash handover desk: the cashier counts cash a waiter hands over and the POS shows the variance the node records; a variance over the
/// facility limit waits for a supervisor's sign-off (permission <c>cash_handover.signoff</c>, and never the person who received it).
/// Also lists the waiters currently holding cash.
/// </summary>
public sealed class CashHandoverDeskViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private readonly INavigator _nav;
    private string _note = string.Empty;

    public CashHandoverDeskViewModel(PosContext ctx, INavigator nav)
    {
        _ctx = ctx;
        _nav = nav;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync), null, SetError);
    }

    public ObservableCollection<HandoverRow> Handovers { get; } = [];

    public ObservableCollection<HoldingRow> Holdings { get; } = [];

    public bool CanReceive => _ctx.Features.CanReceiveHandovers;

    public bool CanSignoff => _ctx.Features.CanSignoffHandovers;

    public int OpenCount => Handovers.Count;

    public bool HasHoldings => Holdings.Count > 0;

    public bool NoHandovers => Handovers.Count == 0;

    /// <summary>Optional note recorded with a sign-off.</summary>
    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    internal void ReportError(Exception ex) => SetError(ex);

    public async Task RefreshQuietlyAsync()
    {
        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // keep the last view
        }
    }

    private async Task LoadAsync()
    {
        var now = _ctx.Time.GetUtcNow();
        var all = await _ctx.Api.ListCashHandoversAsync(_ctx.FacilityId).ConfigureAwait(true);
        var open = all.Where(h => h.IsPendingReceipt || h.IsPendingSignoff).OrderBy(h => h.CreatedAt).ToList();
        Handovers.Clear();
        foreach (var h in open)
        {
            Handovers.Add(new HandoverRow(h, this, now));
        }

        IReadOnlyList<CashInHand>? holdings = null;
        try
        {
            holdings = await _ctx.Api.ListCashInHandAsync(_ctx.FacilityId).ConfigureAwait(true);
        }
        catch (ApiException)
        {
            // no cash_handover.view for the list: the desk still works from the handovers
        }

        if (holdings is null)
        {
            // Older node without the aggregated list: positions of the waiters we can see (those who declared a handover).
            var seen = new List<CashInHand>();
            foreach (var waiter in open.Select(h => h.WaiterStaffId).Distinct())
            {
                try
                {
                    var p = await _ctx.Api.GetCashInHandAsync(waiter).ConfigureAwait(true);
                    seen.Add(p with { WaiterName = open.FirstOrDefault(h => h.WaiterStaffId == waiter)?.WaiterName });
                }
                catch (ApiException)
                {
                    // not allowed to see this position
                }
            }

            holdings = seen;
        }

        Holdings.Clear();
        foreach (var p in holdings.Where(p => p.CashInHandAmount > 0m).OrderByDescending(p => p.CashInHandAmount))
        {
            Holdings.Add(new HoldingRow(p));
        }

        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(NoHandovers));
        OnPropertyChanged(nameof(HasHoldings));
    }

    internal async Task ReceiveAsync(HandoverRow row)
    {
        Error = null;
        var modal = new ReceiveHandoverViewModel(_ctx, row);
        await _nav.ShowModalAsync(modal).ConfigureAwait(true);
        await RefreshQuietlyAsync().ConfigureAwait(true);
        if (modal.Result is { } result)
        {
            Info = result.IsPendingSignoff
                ? $"Received. The variance ({new HandoverRow(result, this, _ctx.Time.GetUtcNow()).VarianceText}) is over the limit: a supervisor must sign it off."
                : $"Received {MoneyFormat.Display(result.CountedAmount ?? 0m)} from {row.WaiterText}. Variance: {new HandoverRow(result, this, _ctx.Time.GetUtcNow()).VarianceText}.";
        }
    }

    internal async Task SignoffAsync(HandoverRow row)
    {
        Error = null;
        await _ctx.Api.SignoffCashHandoverAsync(row.Handover.Id, new SignoffHandoverRequest(string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()), IdempotencyKeys.New()).ConfigureAwait(true);
        Note = string.Empty;
        Info = $"Variance for {row.WaiterText} signed off.";
        await RefreshQuietlyAsync().ConfigureAwait(true);
    }
}

/// <summary>Count a waiter's cash: shows the variance against what they declared before it is recorded.</summary>
public sealed class ReceiveHandoverViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private readonly HandoverRow _row;
    private string _countedText = string.Empty;
    private string _note = string.Empty;
    private string _key = IdempotencyKeys.New();

    public ReceiveHandoverViewModel(PosContext ctx, HandoverRow row)
    {
        _ctx = ctx;
        _row = row;
        ReceiveCommand = new AsyncRelayCommand(ReceiveAsync, () => !IsBusy && Result is null && _ctx.Features.CanReceiveHandovers, SetError);
        CancelCommand = new RelayCommand(() => Close(Result is not null));
    }

    public override string Title => "Receive cash handover";

    public string WaiterText => _row.WaiterText;

    public string DeclaredText => MoneyFormat.Display(_row.Handover.DeclaredAmount);

    public string CountedText
    {
        get => _countedText;
        set
        {
            if (SetProperty(ref _countedText, value))
            {
                _key = IdempotencyKeys.New();
                OnPropertyChanged(nameof(VariancePreview));
                OnPropertyChanged(nameof(HasPreview));
            }
        }
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public bool HasPreview => MoneyFormat.TryParseEnteredAllowZero(CountedText, out _);

    /// <summary>Counted minus declared as the cashier types (display only; the node records the real variance).</summary>
    public string VariancePreview
    {
        get
        {
            if (!MoneyFormat.TryParseEnteredAllowZero(CountedText, out var counted))
            {
                return string.Empty;
            }

            var v = counted - _row.Handover.DeclaredAmount;
            return v == 0m ? "Matches the declared amount." : v < 0m ? "SHORT by " + MoneyFormat.Display(-v) : "OVER by " + MoneyFormat.Display(v);
        }
    }

    public string Guidance => "Count the notes in front of the waiter. A large variance is held for a supervisor's sign-off.";

    public CashHandover? Result { get; private set; }

    public AsyncRelayCommand ReceiveCommand { get; }

    public RelayCommand CancelCommand { get; }

    private async Task ReceiveAsync()
    {
        Error = null;
        if (!MoneyFormat.TryParseEnteredAllowZero(CountedText, out var counted))
        {
            Error = "Enter the counted amount in naira and kobo, e.g. 25000.00 (0 if nothing was handed over).";
            return;
        }

        IsBusy = true;
        ReceiveCommand.RaiseCanExecuteChanged();
        try
        {
            Result = await _ctx.Api.ReceiveCashHandoverAsync(_row.Handover.Id, new ReceiveHandoverRequest(counted, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()), _key).ConfigureAwait(true);
            OnPropertyChanged(nameof(Result));
            Close(true);
        }
        catch (ApiException ex)
        {
            Error = Describe(ex);
            if (ex.Code is "already_received" or "handover_state_invalid")
            {
                Close(false);
            }
        }
        catch (ApiUnavailableException ex)
        {
            Error = Describe(ex) + " Nothing was recorded; press Receive again (it is safe to retry).";
        }
        finally
        {
            IsBusy = false;
            ReceiveCommand.RaiseCanExecuteChanged();
        }
    }
}
