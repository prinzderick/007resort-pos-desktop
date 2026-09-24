using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using R007.Pos.Core.Api;

namespace R007.Pos.Core.Realtime;

/// <summary>A message transport for the realtime client (a WebSocket in production, a script in tests).</summary>
public interface IRealtimeSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>Next complete text message, or <c>null</c> when the peer closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

public sealed class ClientWebSocketAdapter : IRealtimeSocket
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _socket.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(string text, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An application event from a private channel, after envelope parsing and de-duplication.</summary>
public sealed record RealtimeEvent(string Channel, string Name, Guid? EventId, JsonElement Data);

/// <summary>
/// Minimal Laravel Reverb (Pusher protocol 7) client for hints only (see <c>api/realtime.md</c>): it subscribes to
/// <c>private-device.{deviceId}</c> after <c>POST /broadcasting/auth</c>, answers pings, de-duplicates by <c>eventId</c>
/// (delivery is at-least-once) and reconnects with exponential backoff + jitter (1 s to 30 s), re-authorising every
/// channel because the socket id changes. It never mutates business state: consumers re-read REST, and polling keeps
/// working when this client is down.
/// </summary>
public sealed class RealtimeClient(IR007ApiClient api, Func<IRealtimeSocket> socketFactory, Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Random _random = random ?? Random.Shared;
    private readonly Queue<Guid> _seen = new();
    private readonly HashSet<Guid> _seenSet = [];

    public event Action<RealtimeEvent>? EventReceived;

    /// <summary>Raised after every (re)subscription: the consumer MUST reload state over REST (events during the gap are lost).</summary>
    public event Action? Subscribed;

    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// <c>system/info</c> reports the Reverb <c>scheme</c> as <c>http</c>/<c>https</c> (the node's REVERB_SCHEME); a WebSocket needs
    /// <c>ws</c>/<c>wss</c>, so the scheme is mapped here (an already-<c>ws</c> value is kept).
    /// </summary>
    public static Uri BuildUri(RealtimeInfo info)
    {
        var scheme = info.Scheme.ToLowerInvariant() switch
        {
            "https" or "wss" => "wss",
            _ => "ws",
        };
        return new Uri($"{scheme}://{info.Host}:{info.Port}/app/{Uri.EscapeDataString(info.AppKey)}?protocol=7&client=r007-pos&version=0.1&flash=false");
    }

    public async Task RunAsync(RealtimeInfo info, Guid deviceId, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(info, deviceId, ct).ConfigureAwait(false);
                attempt = 0; // a clean close: reconnect promptly
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException or ApiException or ApiUnavailableException or JsonException)
            {
                attempt++;
            }

            ConnectionChanged?.Invoke(false);
            var backoff = Math.Min(30d, Math.Pow(2, Math.Min(attempt, 5))) * (0.5 + _random.NextDouble());
            try
            {
                await _delay(TimeSpan.FromSeconds(Math.Clamp(backoff, 1d, 30d)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(RealtimeInfo info, Guid deviceId, CancellationToken ct)
    {
        await using var socket = socketFactory();
        await socket.ConnectAsync(BuildUri(info), ct).ConfigureAwait(false);
        var channel = $"private-device.{deviceId:D}";

        while (await socket.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var name = root.TryGetProperty("event", out var e) ? e.GetString() : null;
            switch (name)
            {
                case "pusher:connection_established":
                {
                    var socketId = ParseData(root).GetProperty("socket_id").GetString()!;
                    var auth = await api.AuthorizeChannelAsync(socketId, channel, ct).ConfigureAwait(false);
                    await socket.SendAsync(JsonSerializer.Serialize(new { @event = "pusher:subscribe", data = new { auth, channel } }), ct).ConfigureAwait(false);
                    break;
                }

                case "pusher_internal:subscription_succeeded":
                    ConnectionChanged?.Invoke(true);
                    Subscribed?.Invoke();
                    break;
                case "pusher:ping":
                    await socket.SendAsync("""{"event":"pusher:pong","data":{}}""", ct).ConfigureAwait(false);
                    break;
                case "pusher:error":
                    throw new IOException("Realtime server reported an error.");
                case not null when !name.StartsWith("pusher", StringComparison.Ordinal):
                    Dispatch(root, name);
                    break;
                default:
                    break;
            }
        }
    }

    private void Dispatch(JsonElement frame, string name)
    {
        var channel = frame.TryGetProperty("channel", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var envelope = ParseData(frame);
        Guid? eventId = envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("eventId", out var id) && Guid.TryParse(id.GetString(), out var g) ? g : null;
        if (eventId is { } known)
        {
            if (!_seenSet.Add(known))
            {
                return; // duplicate delivery
            }

            _seen.Enqueue(known);
            while (_seen.Count > 500)
            {
                _seenSet.Remove(_seen.Dequeue());
            }
        }

        var data = envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("data", out var d) ? d.Clone() : envelope.Clone();
        EventReceived?.Invoke(new RealtimeEvent(channel, name, eventId, data));
    }

    /// <summary>Pusher frames carry <c>data</c> as a JSON-encoded string (or, tolerantly, an object).</summary>
    private static JsonElement ParseData(JsonElement frame)
    {
        if (!frame.TryGetProperty("data", out var data))
        {
            return default;
        }

        return data.ValueKind == JsonValueKind.String ? JsonDocument.Parse(data.GetString()!).RootElement.Clone() : data.Clone();
    }
}
