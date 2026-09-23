using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.Devices.Printing;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>
/// Cash session (till) open/close and shift report. Closing is a <b>blind count</b>: the cashier declares the counted
/// cash without seeing the system's expected figure; the API then returns expected cash and variance, and the shift
/// report (per-tender totals). All figures shown come from the API.
/// </summary>
public sealed class CashSessionViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private string _openingFloatText = "0.00";
    private string _countedText = string.Empty;
    private string _note = string.Empty;
    private CashierShiftReport? _report;
    private CashSession? _lastClosed;

    public CashSessionViewModel(PosContext ctx)
    {
        _ctx = ctx;
        OpenCommand = new AsyncRelayCommand(OpenAsync, () => !_ctx.HasOpenCashSession && _ctx.Features.CanManageCashSession, SetError);
        CloseCommand = new AsyncRelayCommand(CloseAsync, () => _ctx.HasOpenCashSession, SetError);
        ReportCommand = new AsyncRelayCommand(ReportAsync, () => (_ctx.CashSession ?? _lastClosed) is not null && _ctx.Features.CanViewShiftReport, SetError);
        PrintReportCommand = new AsyncRelayCommand(PrintReportAsync, () => _report is not null, SetError);
        _ctx.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PosContext.CashSession))
            {
                RaiseAll();
            }
        };
    }

    public ObservableCollection<string> ReportLines { get; } = [];

    public bool IsOpen => _ctx.HasOpenCashSession;

    public string StatusText => _ctx.CashSession is { IsOpen: true } s
        ? $"Session open since {s.OpenedAt.ToOffset(TimeSpan.FromHours(1)):dd/MM HH:mm}, opening float {MoneyFormat.Display(s.OpeningFloat)}"
        : "No cash session is open. Open one before taking cash.";

    public string OpeningFloatText
    {
        get => _openingFloatText;
        set => SetProperty(ref _openingFloatText, value);
    }

    public string CountedText
    {
        get => _countedText;
        set => SetProperty(ref _countedText, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public string VarianceText { get; private set; } = string.Empty;

    public AsyncRelayCommand OpenCommand { get; }

    public AsyncRelayCommand CloseCommand { get; }

    public AsyncRelayCommand ReportCommand { get; }

    public AsyncRelayCommand PrintReportCommand { get; }

    public override Task ActivateAsync() => RunAsync(async () =>
    {
        await _ctx.RefreshCashSessionAsync().ConfigureAwait(true);
        RaiseAll();
    });

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsOpen));
        OnPropertyChanged(nameof(StatusText));
        OpenCommand.RaiseCanExecuteChanged();
        CloseCommand.RaiseCanExecuteChanged();
        ReportCommand.RaiseCanExecuteChanged();
    }

    private async Task OpenAsync()
    {
        Error = null;
        if (!MoneyFormat.TryParseEnteredAllowZero(OpeningFloatText, out var floatAmount))
        {
            Error = "Enter the opening float in naira and kobo (0.00 if none).";
            return;
        }

        await RunAsync(async () =>
        {
            _ctx.CashSession = await _ctx.Api.OpenCashSessionAsync(new OpenCashSessionRequest(_ctx.FacilityId, floatAmount), IdempotencyKeys.New()).ConfigureAwait(true);
            VarianceText = string.Empty;
            OnPropertyChanged(nameof(VarianceText));
            ReportLines.Clear();
            Info = "Cash session opened.";
        }).ConfigureAwait(true);
    }

    private async Task CloseAsync()
    {
        Error = null;
        if (!MoneyFormat.TryParseEnteredAllowZero(CountedText, out var counted))
        {
            Error = "Count the cash in the drawer and enter it (naira and kobo).";
            return;
        }

        await RunAsync(async () =>
        {
            var session = _ctx.CashSession!;
            var closed = await _ctx.Api.CloseCashSessionAsync(session.Id, new CloseCashSessionRequest(counted, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()), IdempotencyKeys.New()).ConfigureAwait(true);
            _lastClosed = closed;
            _ctx.CashSession = null;
            VarianceText = closed.Variance is { } v
                ? $"Declared {MoneyFormat.Display(closed.CountedCash ?? counted)}, system {MoneyFormat.Display(closed.ExpectedCash ?? 0m)}, variance {MoneyFormat.Display(v)}"
                : "Closed.";
            OnPropertyChanged(nameof(VarianceText));
            CountedText = string.Empty;
            Info = "Cash session closed.";
            if (_ctx.Features.CanViewShiftReport)
            {
                await LoadReportAsync(closed.Id).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
    }

    private async Task ReportAsync() =>
        await RunAsync(() => LoadReportAsync((_ctx.CashSession ?? _lastClosed)!.Id)).ConfigureAwait(true);

    private async Task LoadReportAsync(Guid sessionId)
    {
        _report = await _ctx.Api.GetShiftReportAsync(sessionId).ConfigureAwait(true);
        ReportLines.Clear();
        foreach (var line in ShiftReportLines(_report))
        {
            ReportLines.Add(line);
        }

        PrintReportCommand.RaiseCanExecuteChanged();
    }

    private async Task PrintReportAsync()
    {
        var doc = new ReceiptDocument(
            [new ReceiptLine("SHIFT REPORT", ReceiptAlignment.Center, true), .. ShiftReportLines(_report!).Select(l => new ReceiptLine(l))],
            true);
        Info = (await _ctx.Printing.PrintAsync(doc).ConfigureAwait(true)).Message;
    }

    /// <summary>Every figure is the API's (expected, counted, variance, per-tender totals).</summary>
    public static IEnumerable<string> ShiftReportLines(CashierShiftReport r)
    {
        yield return $"Cashier: {r.StaffName ?? r.StaffId.ToString()}";
        yield return $"Opened: {r.OpenedAt.ToOffset(TimeSpan.FromHours(1)):dd/MM/yy HH:mm}";
        if (r.ClosedAt is { } closed)
        {
            yield return $"Closed: {closed.ToOffset(TimeSpan.FromHours(1)):dd/MM/yy HH:mm}";
        }

        yield return $"Opening float: {MoneyFormat.Display(r.OpeningFloat)}";
        foreach (var t in r.ByTender)
        {
            yield return $"{t.TenderType}: {MoneyFormat.Display(t.Amount)}{(t.Count is { } c ? $" ({c})" : string.Empty)}";
        }

        if (r.Refunds is { } refunds)
        {
            yield return $"Refunds: {MoneyFormat.Display(refunds)}";
        }

        yield return $"Expected cash: {MoneyFormat.Display(r.ExpectedCash)}";
        if (r.CountedCash is { } counted)
        {
            yield return $"Declared cash: {MoneyFormat.Display(counted)}";
        }

        if (r.Variance is { } variance)
        {
            yield return $"Variance: {MoneyFormat.Display(variance)}";
        }
    }
}
