using R007.Pos.Core.Api;
using R007.Pos.Core.Money;
using R007.Pos.ViewModels.Infrastructure;

namespace R007.Pos.ViewModels.Screens;

/// <summary>Outcome of a sensitive call, normalised across orders/refunds/reversals.</summary>
public sealed record SensitiveResult(bool Applied, Guid? ApprovalId)
{
    public static SensitiveResult From(SensitiveOrderResult r) => new(r.Order is not null, r.Pending?.Approval.Id);

    public static SensitiveResult From(Refund r) => new(!r.IsPendingApproval, r.ApprovalId);

    public static SensitiveResult From(PaymentReversal r) => new(!r.IsPendingApproval, r.ApprovalId);
}

/// <summary>What the operator entered, handed to the API call. <c>StepUpToken</c> is set when a supervisor authorised inline.</summary>
public sealed record SensitiveInput(string? StepUpToken, string Reason, string Value);

public enum SensitiveValueKind
{
    None,
    Percent,
    Amount,
    Price,
}

public enum SensitiveOutcome
{
    None,
    Applied,
    Rejected,
    Cancelled,
    TimedOut,
}

/// <summary>
/// The approval flow for void / discount / comp / price override / refund / reversal. Two ways to authorise, both
/// decided by the API (the POS never decides): (1) "Ask a supervisor": send the request, the API holds it
/// (<c>202</c>), the supervisor decides on their own device, and this dialog waits (poll, with push as a hint);
/// (2) "Supervisor is here": the supervisor authenticates on this terminal (<c>POST /auth/staff/step-up</c>) and
/// the action is repeated with the single-use <c>X-Step-Up-Token</c>, applying immediately.
/// </summary>
public sealed class SensitiveActionViewModel : ModalViewModel
{
    private readonly PosContext _ctx;
    private readonly Func<SensitiveInput, Task<SensitiveResult>> _execute;
    private string _value = string.Empty;
    private readonly string _approvePermission;
    private readonly string? _entityType;
    private readonly Guid? _entityId;
    private CancellationTokenSource? _wait;
    private string _reason = string.Empty;
    private string _supervisorNumber = string.Empty;
    private string _supervisorPin = string.Empty;
    private bool _supervisorHere;
    private bool _isWaiting;
    private string? _waitStatus;
    private Guid? _pendingApprovalId;

    public SensitiveActionViewModel(
        PosContext ctx,
        string title,
        string summary,
        string approvePermission,
        Func<SensitiveInput, Task<SensitiveResult>> execute,
        bool needsReason = true,
        string? entityType = null,
        Guid? entityId = null,
        SensitiveValueKind valueKind = SensitiveValueKind.None)
    {
        ValueKind = valueKind;
        _ctx = ctx;
        Title = title;
        Summary = summary;
        NeedsReason = needsReason;
        _approvePermission = approvePermission;
        _execute = execute;
        _entityType = entityType;
        _entityId = entityId;
        SubmitCommand = new AsyncRelayCommand(SubmitAsync, () => !IsBusy && !IsWaiting, SetError);
        CancelCommand = new AsyncRelayCommand(CancelAsync, onError: SetError);
    }

    public override string Title { get; }

    public string Summary { get; }

    public bool NeedsReason { get; }

    public SensitiveValueKind ValueKind { get; }

    public bool NeedsValue => ValueKind != SensitiveValueKind.None;

    public string ValueLabel => ValueKind switch
    {
        SensitiveValueKind.Percent => "Discount percent (e.g. 10)",
        SensitiveValueKind.Amount => "Discount amount (naira)",
        SensitiveValueKind.Price => "New unit price (naira)",
        _ => string.Empty,
    };

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public SensitiveOutcome Outcome { get; private set; }

    public string Reason
    {
        get => _reason;
        set => SetProperty(ref _reason, value);
    }

    /// <summary>True when the supervisor is at this terminal and authorises with their own credentials.</summary>
    public bool SupervisorHere
    {
        get => _supervisorHere;
        set => SetProperty(ref _supervisorHere, value);
    }

    public string SupervisorNumber
    {
        get => _supervisorNumber;
        set => SetProperty(ref _supervisorNumber, value);
    }

    public string SupervisorPin
    {
        get => _supervisorPin;
        set => SetProperty(ref _supervisorPin, value);
    }

    public bool IsWaiting
    {
        get => _isWaiting;
        private set
        {
            if (SetProperty(ref _isWaiting, value))
            {
                SubmitCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? WaitStatus
    {
        get => _waitStatus;
        private set => SetProperty(ref _waitStatus, value);
    }

    public AsyncRelayCommand SubmitCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    private async Task SubmitAsync()
    {
        Error = null;
        if (NeedsReason && string.IsNullOrWhiteSpace(Reason))
        {
            Error = "A reason is required.";
            return;
        }

        var wireValue = string.Empty;
        if (NeedsValue && !TryValue(out wireValue))
        {
            Error = ValueKind == SensitiveValueKind.Percent ? "Enter a percentage between 0 and 100." : "Enter an amount in naira and kobo, e.g. 500.00.";
            return;
        }

        string? stepUp = null;
        IsBusy = true;
        try
        {
            if (SupervisorHere)
            {
                if (string.IsNullOrWhiteSpace(SupervisorNumber) || string.IsNullOrWhiteSpace(SupervisorPin))
                {
                    Error = "The supervisor must enter their staff number and PIN.";
                    return;
                }

                var granted = await _ctx.Api.StepUpAsync(new StepUpRequest(CredentialTypes.Pin, SupervisorNumber.Trim(), SupervisorPin, _approvePermission, _entityType, _entityId)).ConfigureAwait(true);
                stepUp = granted.StepUpToken;
                SupervisorPin = string.Empty; // never keep a credential around
            }

            var result = await _execute(new SensitiveInput(stepUp, Reason.Trim(), wireValue)).ConfigureAwait(true);
            if (result.Applied)
            {
                Finish(SensitiveOutcome.Applied);
                return;
            }

            if (result.ApprovalId is not { } approvalId)
            {
                Error = "The server did not confirm the action. Please check the order.";
                return;
            }

            _pendingApprovalId = approvalId;
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            Error = Describe(ex);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await WaitForSupervisorAsync().ConfigureAwait(true);
    }

    private bool TryValue(out string wire)
    {
        wire = string.Empty;
        if (ValueKind == SensitiveValueKind.Percent)
        {
            if (!MoneyFormat.TryParseWire(Value, out var percent) || percent <= 0m || percent > 100m)
            {
                return false;
            }

            wire = MoneyFormat.ToWire(percent);
            return true;
        }

        if (!MoneyFormat.TryParseEntered(Value, out var amount))
        {
            return false;
        }

        wire = MoneyFormat.ToWire(amount);
        return true;
    }

    private async Task WaitForSupervisorAsync()
    {
        var approval = await _ctx.Api.GetApprovalAsync(_pendingApprovalId!.Value).ConfigureAwait(true);
        IsWaiting = true;
        WaitStatus = "Waiting for a supervisor to approve on their device...";
        _wait = new CancellationTokenSource();
        try
        {
            var result = await _ctx.Approvals.WaitForDecisionAsync(approval, a => WaitStatus = $"Waiting for a supervisor... ({a.Status})", _wait.Token).ConfigureAwait(true);
            if (result.TimedOut)
            {
                Error = "No supervisor responded in time. The request is still pending; check Approvals later.";
                Finish(SensitiveOutcome.TimedOut);
            }
            else if (result.Approved)
            {
                Finish(SensitiveOutcome.Applied);
            }
            else
            {
                Error = string.IsNullOrWhiteSpace(result.Approval.DecisionNote)
                    ? $"The supervisor did not approve this ({result.Approval.Status.ToLowerInvariant()})."
                    : $"Not approved: {result.Approval.DecisionNote}";
                Finish(SensitiveOutcome.Rejected);
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by the operator: CancelAsync handles the API side
        }
        finally
        {
            IsWaiting = false;
        }
    }

    private async Task CancelAsync()
    {
        if (IsWaiting && _pendingApprovalId is { } id)
        {
            await _wait!.CancelAsync().ConfigureAwait(true);
            try
            {
                await _ctx.Api.CancelApprovalAsync(id, IdempotencyKeys.New()).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
            {
                // Already decided or server unreachable: the order screen refresh shows the truth.
            }

            Finish(SensitiveOutcome.Cancelled);
            return;
        }

        Finish(SensitiveOutcome.Cancelled);
    }

    private void Finish(SensitiveOutcome outcome)
    {
        Outcome = outcome;
        Close(outcome == SensitiveOutcome.Applied);
    }
}
