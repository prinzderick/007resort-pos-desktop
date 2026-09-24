namespace R007.Pos.Core.Offline;

/// <summary>
/// An API call captured while the site API was unreachable.
/// </summary>
/// <param name="IdempotencyKey">Client-generated key sent as the <c>Idempotency-Key</c> header on replay,
/// so the API applies the operation at most once.</param>
/// <param name="Method">HTTP method (e.g. <c>POST</c>).</param>
/// <param name="RelativePath">API path relative to the base address, e.g. <c>api/v1/payments</c>.</param>
/// <param name="JsonBody">Request body as JSON (encrypted at rest by the queue implementation).</param>
/// <param name="CreatedAtUtc">When the operation was captured (UTC).</param>
/// <param name="StaffId">Staff member who captured it (sent to the API as context on replay; the API decides what to do with it).</param>
/// <param name="Description">Short human description for the "pending confirmation" list (contains no card data).</param>
/// <param name="AggregatePath">Relative GET path of the aggregate this operation touches (e.g. <c>api/v1/orders/{id}</c>).
/// Replay tracks each aggregate's <c>rowVersion</c> from earlier responses so later operations get a correct <c>If-Match</c>.</param>
/// <param name="NeedsIfMatch">True when the API call requires <c>If-Match</c> (line add/remove, send).</param>
public sealed record QueuedOperation(
    Guid IdempotencyKey,
    string Method,
    string RelativePath,
    string JsonBody,
    DateTimeOffset CreatedAtUtc,
    Guid? StaffId = null,
    string? Description = null,
    string? AggregatePath = null,
    bool NeedsIfMatch = false);

/// <summary>An entry the API refused on replay (business rule failed, e.g. the balance changed). Shown to staff, never dropped silently.</summary>
public sealed record RejectedOperation(QueuedOperation Operation, string Reason, DateTimeOffset RejectedAtUtc);

public sealed record QueueStatus(int PendingCount, TimeSpan? OldestPendingAge, bool IsBlocked, string? BlockedReason, int UnacknowledgedRejectedCount, bool CorruptionDetected);

public sealed class OfflineQueueOptions
{
    /// <summary>Maximum entries waiting to replay before new sensitive actions are blocked.</summary>
    public int MaxEntries { get; set; } = 200;

    /// <summary>Oldest pending entry age beyond which new sensitive actions are blocked (offline-strategy §3).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Thrown by <c>EnqueueAsync</c> when the bounded queue is full or too stale to accept more.</summary>
public sealed class OfflineQueueBlockedException(string message) : InvalidOperationException(message);
