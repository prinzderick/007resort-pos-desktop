using R007.Pos.Core.Api;

namespace R007.Pos.ViewModels.Services;

/// <summary>What the coordinator needs from the server (kept narrow so it can be tested without HTTP).</summary>
public interface IPrintJobSource
{
    Task<IReadOnlyList<PrintJob>> ListQueuedAsync(CancellationToken ct);

    Task MarkPrintedAsync(Guid jobId, CancellationToken ct);
}

/// <summary>Production source: the API client scoped to this terminal's facility.</summary>
public sealed class ApiPrintJobSource(IR007ApiClient api, Func<Guid?> facility) : IPrintJobSource
{
    public async Task<IReadOnlyList<PrintJob>> ListQueuedAsync(CancellationToken ct) =>
        facility() is { } id ? await api.ListQueuedPrintJobsAsync(id, ct).ConfigureAwait(false) : [];

    public Task MarkPrintedAsync(Guid jobId, CancellationToken ct) =>
        api.MarkPrintJobPrintedAsync(jobId, Guid.NewGuid().ToString("N"), ct);
}

/// <summary>
/// Waiter-only flow: a tablet taps "Send receipt to printer"; this terminal (which has the printer) picks the job up, prints the
/// receipt the requested number of times and marks the job printed. A job that cannot be printed (no paper, printer off) stays
/// queued and is retried on the next pass; it never blocks selling.
/// </summary>
public sealed class PrintQueueCoordinator(IPrintJobSource source, Func<Guid, int, CancellationToken, Task<bool>> print, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(8);

    /// <summary>One pass over the queue. Returns how many jobs were printed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<PrintJob> jobs;
        try
        {
            jobs = await source.ListQueuedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
        {
            return 0; // no connection or no permission: try again next pass
        }

        var done = 0;
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (!await print(job.ReceiptId, Math.Max(1, job.Copies), ct).ConfigureAwait(false))
            {
                break; // printer trouble: stop this pass, keep the rest queued in order
            }

            try
            {
                await source.MarkPrintedAsync(job.Id, ct).ConfigureAwait(false);
                done++;
            }
            catch (Exception ex) when (ex is ApiException or ApiUnavailableException)
            {
                // Printed but not confirmed: another pass may print it again; a duplicate slip is better than a lost one.
                break;
            }
        }

        return done;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
                await _delay(Interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // The poller must never take the till down; wait and try again.
                try
                {
                    await _delay(Interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
