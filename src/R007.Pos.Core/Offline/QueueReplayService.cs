using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Http;

namespace R007.Pos.Core.Offline;

public enum ReplayStop
{
    None,
    NotSignedIn,
    ServerUnavailable,
}

public sealed record ReplayResult(int Replayed, int Rejected, int Remaining, ReplayStop Stopped);

/// <summary>
/// Replays the emergency queue through the API only, strictly in capture order, one request in flight.
/// Each request reuses its original <c>Idempotency-Key</c>, so a crash between "server applied it" and "marked
/// replayed" is harmless. A transient failure (network, 5xx) stops the drain to preserve ordering; a definite 4xx
/// business refusal marks the entry rejected (kept for staff to see) and continues with the next entry.
/// </summary>
public sealed class QueueReplayService(IOfflineQueue queue, HttpClient http, AuthState auth)
{
    public const string CapturedAtHeader = "X-Offline-Captured-At";
    public const string CapturedByHeader = "X-Offline-Staff-Id";

    private readonly object _gate = new();
    private Task<ReplayResult>? _running;

    // aggregate GET path -> last known rowVersion (this drain only; always re-derived from server truth)
    private readonly Dictionary<string, int> _versions = [];

    public event EventHandler<ReplayResult>? Drained;

    /// <summary>
    /// Drains the queue. Only one drain runs at a time (strict ordering); a caller arriving while one is in progress
    /// simply waits for it and shares its result.
    /// </summary>
    public Task<ReplayResult> DrainAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return _running ??= RunAsync(ct);
        }
    }

    private async Task<ReplayResult> RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Yield(); // let the caller store the task before any work starts
            var replayed = 0;
            var rejected = 0;
            var stop = ReplayStop.None;
            _versions.Clear();

            foreach (var op in await queue.GetPendingAsync(ct).ConfigureAwait(false))
            {
                if (!auth.IsSignedIn)
                {
                    stop = ReplayStop.NotSignedIn;
                    break;
                }

                var outcome = await SendAsync(op, ct).ConfigureAwait(false);
                if (outcome.Kind == OutcomeKind.Success)
                {
                    await queue.MarkReplayedAsync(op.IdempotencyKey, ct).ConfigureAwait(false);
                    replayed++;
                }
                else if (outcome.Kind == OutcomeKind.Rejected)
                {
                    await queue.MarkRejectedAsync(op.IdempotencyKey, outcome.Reason!, ct).ConfigureAwait(false);
                    rejected++;
                }
                else
                {
                    stop = outcome.Kind == OutcomeKind.NotSignedIn ? ReplayStop.NotSignedIn : ReplayStop.ServerUnavailable;
                    break;
                }
            }

            var result = new ReplayResult(replayed, rejected, await queue.CountPendingAsync(ct).ConfigureAwait(false), stop);
            Drained?.Invoke(this, result);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
            }
        }
    }

    private async Task<Outcome> SendAsync(QueuedOperation op, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(op.Method), new Uri(op.RelativePath, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(R007ApiClient.IdempotencyHeader, op.IdempotencyKey.ToString("D"));
        request.Headers.TryAddWithoutValidation(CapturedAtHeader, op.CreatedAtUtc.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (op.StaffId is { } staff)
        {
            request.Headers.TryAddWithoutValidation(CapturedByHeader, staff.ToString("D"));
        }

        if (op.NeedsIfMatch && op.AggregatePath is { } aggregate)
        {
            var version = await CurrentVersionAsync(aggregate, ct).ConfigureAwait(false);
            if (version is null)
            {
                return new Outcome(OutcomeKind.Transient, null);
            }

            request.Headers.TryAddWithoutValidation("If-Match", R007ApiClient.ETag(version.Value));
        }

        if (!string.IsNullOrEmpty(op.JsonBody))
        {
            request.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(op.JsonBody));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return new Outcome(OutcomeKind.Transient, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Outcome(OutcomeKind.Transient, null);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                await TrackVersionAsync(op, response, ct).ConfigureAwait(false);
                return new Outcome(OutcomeKind.Success, null);
            }

            var code = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new Outcome(OutcomeKind.NotSignedIn, null);
            }

            if (code >= 500 || code is 408 or 425 or 429)
            {
                return new Outcome(OutcomeKind.Transient, null);
            }

            return new Outcome(OutcomeKind.Rejected, await ReasonAsync(response, ct).ConfigureAwait(false));
        }
    }

    private async Task<int?> CurrentVersionAsync(string aggregatePath, CancellationToken ct)
    {
        if (_versions.TryGetValue(aggregatePath, out var known))
        {
            return known;
        }

        try
        {
            using var response = await http.GetAsync(new Uri(aggregatePath, UriKind.Relative), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var version = ReadRowVersion(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            if (version is { } v)
            {
                _versions[aggregatePath] = v;
            }

            return version;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task TrackVersionAsync(QueuedOperation op, HttpResponseMessage response, CancellationToken ct)
    {
        if (op.AggregatePath is null)
        {
            return;
        }

        var version = ReadRowVersion(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        if (version is { } v)
        {
            _versions[op.AggregatePath] = v;
        }
    }

    private static int? ReadRowVersion(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("rowVersion", out var rv)
                && rv.TryGetInt32(out var value)
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var problem = JsonSerializer.Deserialize(bytes, PosJsonContext.Default.ProblemDetailsDto);
            if (problem is not null)
            {
                return $"{problem.Code ?? $"http_{(int)response.StatusCode}"}: {problem.Detail ?? problem.Title}";
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return $"http_{(int)response.StatusCode}";
    }

    private enum OutcomeKind
    {
        Success,
        Rejected,
        Transient,
        NotSignedIn,
    }

    private sealed record Outcome(OutcomeKind Kind, string? Reason);
}
