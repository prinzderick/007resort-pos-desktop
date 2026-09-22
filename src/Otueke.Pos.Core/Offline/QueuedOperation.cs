namespace Otueke.Pos.Core.Offline;

/// <summary>
/// An API call captured while the site API was unreachable.
/// </summary>
/// <param name="IdempotencyKey">Client-generated key sent as the <c>Idempotency-Key</c> header on replay,
/// so the API applies the operation at most once.</param>
/// <param name="Method">HTTP method (e.g. <c>POST</c>).</param>
/// <param name="RelativePath">API path relative to the base address, e.g. <c>api/v1/orders</c>.</param>
/// <param name="JsonBody">Request body as JSON (encrypted at rest by the queue implementation).</param>
/// <param name="CreatedAtUtc">When the operation was captured (UTC).</param>
public sealed record QueuedOperation(
    Guid IdempotencyKey,
    string Method,
    string RelativePath,
    string JsonBody,
    DateTimeOffset CreatedAtUtc);
