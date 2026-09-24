using System.Collections.ObjectModel;
using R007.Pos.Core.Offline;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

public sealed class RejectedRow(RejectedOperation op, QueueViewModel owner)
{
    public string Description => op.Operation.Description ?? op.Operation.RelativePath;

    public string Reason => op.Reason;

    public string When => op.RejectedAtUtc.ToOffset(TimeSpan.FromHours(1)).ToString("dd/MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public AsyncRelayCommand AcknowledgeCommand { get; } = new(() => owner.AcknowledgeAsync(op), null, owner.ReportError);
}

/// <summary>The encrypted emergency queue: what is waiting to be confirmed, and what the server refused (never silently dropped).</summary>
public sealed class QueueViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private QueueStatus? _status;

    public QueueViewModel(PosContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync), null, SetError);
        DrainCommand = new AsyncRelayCommand(DrainAsync, () => _ctx.Auth.IsSignedIn, SetError);
    }

    public ObservableCollection<string> Pending { get; } = [];

    public ObservableCollection<RejectedRow> Rejected { get; } = [];

    public string SummaryText => _status is null
        ? string.Empty
        : _status.PendingCount == 0 && _status.UnacknowledgedRejectedCount == 0
            ? "Nothing waiting."
            : $"{_status.PendingCount} waiting to be confirmed" + (_status.OldestPendingAge is { } age ? $" (oldest {age.TotalMinutes:0} min)" : string.Empty)
              + (_status.UnacknowledgedRejectedCount > 0 ? $", {_status.UnacknowledgedRejectedCount} refused by the server" : string.Empty);

    public string? BlockedText => _status?.BlockedReason;

    public bool CorruptionDetected => _status?.CorruptionDetected == true;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand DrainCommand { get; }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    internal void ReportError(Exception ex) => SetError(ex);

    /// <summary>Reloads the lists. Loads are serialised (the shell refreshes this screen from background events too) and the collections are swapped in one synchronous step.</summary>
    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var status = await _ctx.Queue.GetStatusAsync().ConfigureAwait(true);
            var pending = await _ctx.Queue.GetPendingAsync().ConfigureAwait(true);
            var rejected = await _ctx.Queue.GetRejectedAsync().ConfigureAwait(true);

            _status = status;
            Pending.Clear();
            foreach (var op in pending)
            {
                Pending.Add(op.Description ?? op.RelativePath);
            }

            Rejected.Clear();
            foreach (var r in rejected)
            {
                Rejected.Add(new RejectedRow(r, this));
            }

            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(BlockedText));
            OnPropertyChanged(nameof(CorruptionDetected));
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task DrainAsync()
    {
        var result = await _ctx.Replay.DrainAsync().ConfigureAwait(true);
        Info = result.Stopped switch
        {
            ReplayStop.NotSignedIn => "Sign in to send waiting items.",
            ReplayStop.ServerUnavailable => "Server still unreachable. Will retry.",
            _ => $"Sent {result.Replayed}, refused {result.Rejected}, {result.Remaining} remaining.",
        };
        await LoadAsync().ConfigureAwait(true);
    }

    internal async Task AcknowledgeAsync(RejectedOperation op)
    {
        await _ctx.Queue.AcknowledgeRejectedAsync(op.Operation.IdempotencyKey).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }
}
