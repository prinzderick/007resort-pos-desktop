using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Http;

namespace R007.Pos.Core.Offline;

/// <summary>What happened to an operation staff tried to perform while the API was unreachable.</summary>
public enum EmergencyOutcome
{
    /// <summary>Captured for ordered replay. NOT confirmed: the UI must show "pending confirmation", never "paid".</summary>
    Queued,

    /// <summary>The operation type is not allowed to be queued (e.g. anything needing provider authorisation).</summary>
    NotQueueable,

    /// <summary>The bounded queue is full or too stale; reconnect first.</summary>
    Blocked,
}

public sealed record EmergencyResult(EmergencyOutcome Outcome, string Message)
{
    public bool IsQueued => Outcome == EmergencyOutcome.Queued;
}

/// <summary>Facility operating rules for offline work (<c>allowOfflineOrders</c>, <c>allowOfflinePayments</c>).</summary>
public sealed record OfflinePolicy(bool AllowOrders, string Payments)
{
    public static OfflinePolicy Disabled { get; } = new(false, OfflinePaymentPolicy.None);

    public bool AllowsCashPayments => Payments is OfflinePaymentPolicy.CashOnly or OfflinePaymentPolicy.All;
}

/// <summary>
/// Client-side gate + writer for the emergency queue. It decides only "is this kind of request safe to hold
/// blind" (an allow-list of API calls whose validity the API re-checks on replay, per the contract's offline
/// guidance); it never marks anything successful. Allowed: create order (client UUIDv7 id), add line (client line id),
/// send order, open tab (client id), and payments made only of <b>CASH</b> tenders (client tender ids), each subject
/// to the facility's offline policy. Never queued: card, POS terminal, transfer, Paystack (need authorisation),
/// refunds/reversals, voids, adjustments, approvals, cash-session open/close, booking holds, redemption.
/// </summary>
public sealed class EmergencyQueue(IOfflineQueue queue, AuthState auth, TimeProvider time)
{
    /// <summary>Set after sign-in from the facility's operating rules.</summary>
    public OfflinePolicy Policy { get; set; } = OfflinePolicy.Disabled;

    public bool IsQueueable(string method, string relativePath, string? jsonBody)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = relativePath.TrimStart('/');
        var parts = path.Split('/');

        // api/v1/orders  |  api/v1/tabs
        if (parts.Length == 3 && parts[0] == "api" && parts[1] == "v1" && parts[2] is "orders" or "tabs")
        {
            return Policy.AllowOrders;
        }

        // api/v1/orders/{id}/lines  |  api/v1/orders/{id}/send
        if (parts.Length == 5 && parts[2] == "orders" && parts[4] is "lines" or "send")
        {
            return Policy.AllowOrders;
        }

        if (path == "api/v1/payments" && jsonBody is not null && Policy.AllowsCashPayments)
        {
            try
            {
                var payment = JsonSerializer.Deserialize(jsonBody, PosJsonContext.Default.CreatePaymentRequest);
                return payment is { Tenders.Count: > 0 } && payment.Tenders.All(t => t.TenderType == TenderTypes.Cash);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    public Task<EmergencyResult> QueueAsync(
        string relativePath,
        string jsonBody,
        string idempotencyKey,
        string description,
        string? aggregatePath,
        bool needsIfMatch,
        CancellationToken ct = default)
    {
        const string method = "POST";
        if (!IsQueueable(method, relativePath, jsonBody))
        {
            return Task.FromResult(new EmergencyResult(EmergencyOutcome.NotQueueable, "This action cannot be completed while the server is unreachable."));
        }

        if (!Guid.TryParse(idempotencyKey, out var key))
        {
            throw new ArgumentException("Idempotency key must be a GUID.", nameof(idempotencyKey));
        }

        return EnqueueAsync(new QueuedOperation(key, method, relativePath.TrimStart('/'), jsonBody, time.GetUtcNow(), auth.Staff?.Id, description, aggregatePath, needsIfMatch), ct);
    }

    private async Task<EmergencyResult> EnqueueAsync(QueuedOperation op, CancellationToken ct)
    {
        try
        {
            await queue.EnqueueAsync(op, ct).ConfigureAwait(false);
        }
        catch (OfflineQueueBlockedException ex)
        {
            return new EmergencyResult(EmergencyOutcome.Blocked, ex.Message);
        }

        return new EmergencyResult(EmergencyOutcome.Queued, "Saved on this terminal. NOT confirmed yet: it will be sent to the server when the connection returns.");
    }
}
