using System.Collections.Concurrent;
using R007.Pos.Core.Api;
using R007.Pos.Core.Configuration;

namespace R007.Pos.ViewModels.Services;

public sealed record ApprovalWaitResult(Approval Approval, bool TimedOut)
{
    public bool Approved => Approval.Status == ApprovalStatuses.Approved;
}

/// <summary>
/// Waits for a supervisor's decision on a pending approval. Polls <c>GET /approvals/{id}</c> (the source of truth);
/// a realtime push (<c>approval.decided</c> on <c>private-device.{id}</c>) can shorten the wait through
/// <see cref="NotifyDecided"/> without changing correctness, because a push is only ever a hint to re-read state.
/// </summary>
public sealed class ApprovalCoordinator(IR007ApiClient api, ApprovalOptions options, TimeProvider time, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _hints = new();

    /// <summary>A push arrived for this approval: poll now instead of waiting out the interval.</summary>
    public void NotifyDecided(Guid approvalId)
    {
        if (_hints.TryGetValue(approvalId, out var tcs))
        {
            tcs.TrySetResult();
        }
    }

    public async Task<ApprovalWaitResult> WaitForDecisionAsync(Approval approval, Action<Approval>? onUpdate = null, CancellationToken ct = default)
    {
        var deadline = time.GetUtcNow().AddMinutes(options.WaitTimeoutMinutes);
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.PollIntervalSeconds));
        var current = approval;

        while (current.IsPending)
        {
            var hint = _hints.GetOrAdd(approval.Id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            try
            {
                await Task.WhenAny(_delay(interval, ct), hint.Task).ConfigureAwait(true);
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                _hints.TryRemove(approval.Id, out _);
            }

            try
            {
                current = await api.GetApprovalAsync(approval.Id, ct).ConfigureAwait(true);
                onUpdate?.Invoke(current);
            }
            catch (ApiUnavailableException)
            {
                // Transient: keep waiting; the request stays pending on the server.
            }

            if (current.IsPending && time.GetUtcNow() >= deadline)
            {
                return new ApprovalWaitResult(current, TimedOut: true);
            }
        }

        return new ApprovalWaitResult(current, TimedOut: false);
    }
}
