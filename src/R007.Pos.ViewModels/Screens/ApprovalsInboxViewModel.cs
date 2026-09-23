using System.Collections.ObjectModel;
using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

public sealed class ApprovalRow(Approval approval, ApprovalsInboxViewModel owner)
{
    public Approval Approval { get; } = approval;

    public string Summary => string.IsNullOrWhiteSpace(Approval.Summary) ? Approval.Action : Approval.Summary;

    public string RequestedBy => $"{Approval.RequestedByName ?? "Staff"}  {Approval.RequestedAt.ToLocalTime():HH:mm}";

    public string Reason => Approval.Reason;

    public string AmountText => Approval.Amount is { } a ? MoneyFormat.Display(a) : string.Empty;

    public AsyncRelayCommand ApproveCommand { get; } = new(() => owner.DecideAsync(approval, ApprovalDecisions.Approve), null, owner.ReportError);

    public AsyncRelayCommand RejectCommand { get; } = new(() => owner.DecideAsync(approval, ApprovalDecisions.Reject), null, owner.ReportError);
}

/// <summary>Supervisor inbox: approvals waiting for a decision from someone who holds the approve permission.</summary>
public sealed class ApprovalsInboxViewModel : ScreenViewModel
{
    private readonly PosContext _ctx;
    private string _note = string.Empty;

    public ApprovalsInboxViewModel(PosContext ctx)
    {
        _ctx = ctx;
        RefreshCommand = new AsyncRelayCommand(() => RunAsync(LoadAsync), null, SetError);
    }

    public ObservableCollection<ApprovalRow> Pending { get; } = [];

    /// <summary>Optional note attached to the next decision.</summary>
    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
    }

    public int PendingCount => Pending.Count;

    public AsyncRelayCommand RefreshCommand { get; }

    public override Task ActivateAsync() => RunAsync(LoadAsync);

    internal void ReportError(Exception ex) => SetError(ex);

    /// <summary>Quiet refresh for the shell's badge / auto-refresh loop (network errors are not shown as errors).</summary>
    public async Task RefreshQuietlyAsync()
    {
        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            // keep the last list
        }
    }

    private async Task LoadAsync()
    {
        var items = await _ctx.Api.ListApprovalsAsync("approvable").ConfigureAwait(true);
        Pending.Clear();
        foreach (var a in items)
        {
            Pending.Add(new ApprovalRow(a, this));
        }

        OnPropertyChanged(nameof(PendingCount));
    }

    internal async Task DecideAsync(Approval approval, string decision)
    {
        Error = null;
        await _ctx.Api.DecideApprovalAsync(approval.Id, new ApprovalDecisionRequest(decision, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()), IdempotencyKeys.New()).ConfigureAwait(true);
        Note = string.Empty;
        Info = decision == ApprovalDecisions.Approve ? "Approved." : "Rejected.";
        await LoadAsync().ConfigureAwait(true);
    }
}
