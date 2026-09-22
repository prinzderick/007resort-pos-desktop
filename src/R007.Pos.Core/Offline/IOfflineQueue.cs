namespace R007.Pos.Core.Offline;

/// <summary>
/// Emergency queue for operations captured while the site API is unreachable.
/// </summary>
/// <remarks>
/// <para>This is <b>not</b> a local database and holds no master data. Rules for implementations:</para>
/// <list type="bullet">
///   <item><description><b>Encrypted at rest</b> (e.g. DPAPI-protected key, per-device), never plain text.</description></item>
///   <item><description><b>Append-only</b>: entries are never edited; they are only marked replayed.</description></item>
///   <item><description>Every entry carries an <b>idempotency key</b> so replays cannot duplicate effects.</description></item>
///   <item><description>Entries are <b>replayed only through the API</b>, in order; the API re-validates every
///   business rule and may reject an entry (which is then surfaced to staff, not silently dropped).</description></item>
///   <item><description>Which operations may be queued offline is decided by API policy, not by the client.</description></item>
/// </list>
/// </remarks>
public interface IOfflineQueue
{
    /// <summary>Appends an operation to the queue.</summary>
    Task EnqueueAsync(QueuedOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Returns pending (not yet replayed) operations in capture order.</summary>
    Task<IReadOnlyList<QueuedOperation>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks an operation as successfully replayed through the API.</summary>
    Task MarkReplayedAsync(Guid idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Number of operations still waiting to be replayed.</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);
}
