namespace R007.Pos.Core.Offline;

/// <summary>
/// Emergency queue for operations captured while the site API is unreachable.
/// </summary>
/// <remarks>
/// <para>This is <b>not</b> a local database and holds no master data. Rules for implementations:</para>
/// <list type="bullet">
///   <item><description><b>Encrypted at rest</b> (AES-256-GCM, key protected by DPAPI), never plain text.</description></item>
///   <item><description><b>Append-only</b>: entries are never edited; resolution is recorded as a new record.</description></item>
///   <item><description>Every entry carries an <b>idempotency key</b> so replays cannot duplicate effects.</description></item>
///   <item><description>Entries are <b>replayed only through the API</b>, in order; the API re-validates every
///   business rule and may reject an entry (which is then surfaced to staff, not silently dropped).</description></item>
///   <item><description><b>Bounded</b> by count and age; when bounded out, enqueueing throws
///   <see cref="OfflineQueueBlockedException"/> so the UI blocks new sensitive actions.</description></item>
/// </list>
/// </remarks>
public interface IOfflineQueue
{
    /// <summary>Appends an operation. A repeated idempotency key is ignored (already captured).</summary>
    Task EnqueueAsync(QueuedOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Returns pending (not yet resolved) operations in capture order.</summary>
    Task<IReadOnlyList<QueuedOperation>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks an operation as successfully replayed through the API.</summary>
    Task MarkReplayedAsync(Guid idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Marks an operation as refused by the API on replay; it stays visible until acknowledged.</summary>
    Task MarkRejectedAsync(Guid idempotencyKey, string reason, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RejectedOperation>> GetRejectedAsync(CancellationToken cancellationToken = default);

    /// <summary>Staff have seen the rejection; it no longer counts as needing attention.</summary>
    Task AcknowledgeRejectedAsync(Guid idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Number of operations still waiting to be replayed.</summary>
    Task<int> CountPendingAsync(CancellationToken cancellationToken = default);

    Task<QueueStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
