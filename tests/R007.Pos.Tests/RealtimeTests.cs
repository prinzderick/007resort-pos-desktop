using System.Collections.Concurrent;
using System.Text.Json;
using R007.Pos.Core.Api;
using R007.Pos.Core.Mock;
using R007.Pos.Core.Realtime;
using R007.Pos.ViewModels;

namespace R007.Pos.Tests;

public sealed class RealtimeTests
{
    /// <summary>A scripted Pusher server: each connection replays the messages queued for it, then closes (null).</summary>
    private sealed class FakeSocket(ConcurrentQueue<string?> incoming, List<string> sent, List<Uri> connections) : IRealtimeSocket
    {
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            connections.Add(uri);
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            lock (sent)
            {
                sent.Add(text);
            }

            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (incoming.TryDequeue(out var next))
                {
                    return next;
                }

                await Task.Delay(1, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string Established(string socketId) =>
        JsonSerializer.Serialize(new { @event = "pusher:connection_established", data = JsonSerializer.Serialize(new { socket_id = socketId, activity_timeout = 120 }) });

    private static string Event(string channel, string name, Guid eventId, object data) =>
        JsonSerializer.Serialize(new { @event = name, channel, data = JsonSerializer.Serialize(new { eventId, occurredAt = "2026-09-23T10:00:00Z", data }) });

    private static readonly RealtimeInfo Info = new("ws", "10.0.0.5", 8081, "reverb-key");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "condition not reached");
    }

    [Fact]
    public void Uri_UsesProtocol7_AndTheAppKey()
    {
        Assert.Equal("ws://10.0.0.5:8081/app/reverb-key?protocol=7&client=r007-pos&version=0.1&flash=false", RealtimeClient.BuildUri(Info).ToString());
    }

    [Fact]
    public async Task Handshake_AuthorisesTheDeviceChannel_Subscribes_AndDeliversEventsOnce()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var deviceId = Guid.NewGuid();
        var incoming = new ConcurrentQueue<string?>();
        var sent = new List<string>();
        var connections = new List<Uri>();
        var received = new List<RealtimeEvent>();
        var subscribed = 0;
        var client = new RealtimeClient(env.Api, () => new FakeSocket(incoming, sent, connections), (_, ct) => Task.Delay(Timeout.Infinite, ct));
        client.EventReceived += received.Add;
        client.Subscribed += () => subscribed++;
        var channel = $"private-device.{deviceId:D}";
        var duplicate = Guid.NewGuid();
        incoming.Enqueue(Established("1234.5678"));
        incoming.Enqueue("""{"event":"pusher_internal:subscription_succeeded","channel":"x","data":"{}"}""");
        incoming.Enqueue("""{"event":"pusher:ping","data":"{}"}""");
        incoming.Enqueue(Event(channel, "approval.decided", duplicate, new { approval = new { id = Guid.NewGuid(), status = "APPROVED" } }));
        incoming.Enqueue(Event(channel, "approval.decided", duplicate, new { approval = new { id = Guid.NewGuid(), status = "APPROVED" } })); // at-least-once redelivery
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(Info, deviceId, cts.Token);
        await WaitUntilAsync(() => received.Count >= 1 && sent.Count >= 2);
        await Task.Delay(30);
        cts.Cancel();
        await run;

        Assert.Single(received);
        Assert.Equal("approval.decided", received[0].Name);
        Assert.Equal("APPROVED", received[0].Data.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal(1, subscribed);
        var subscribe = JsonDocument.Parse(sent[0]).RootElement;
        Assert.Equal("pusher:subscribe", subscribe.GetProperty("event").GetString());
        Assert.Equal(channel, subscribe.GetProperty("data").GetProperty("channel").GetString());
        Assert.StartsWith("mockkey:", subscribe.GetProperty("data").GetProperty("auth").GetString(), StringComparison.Ordinal);
        Assert.Contains(sent, m => m.Contains("pusher:pong", StringComparison.Ordinal));
        // The auth call carried the staff session and device token like any other request.
        var auth = env.Server.Requests.Single(r => r.Path == "/api/v1/broadcasting/auth");
        Assert.Contains("\"socket_id\":\"1234.5678\"", auth.Body, StringComparison.Ordinal);
        Assert.StartsWith("Bearer at-", auth.Headers["Authorization"]);
    }

    [Fact]
    public async Task ExtraChannels_AreAuthorised_AndFacilityPaymentEventsAreDelivered_EvenIfTheyAreRefusedLater()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var deviceId = Guid.NewGuid();
        var facility = Guid.NewGuid();
        var incoming = new ConcurrentQueue<string?>();
        var sent = new List<string>();
        var received = new List<RealtimeEvent>();
        var client = new RealtimeClient(env.Api, () => new FakeSocket(incoming, sent, []), (_, ct) => Task.Delay(Timeout.Infinite, ct));
        client.EventReceived += received.Add;
        var facilityChannel = $"private-facility.{facility:D}.orders";
        incoming.Enqueue(Established("9.9"));
        incoming.Enqueue("""{"event":"pusher_internal:subscription_succeeded","channel":"x","data":"{}"}""");
        incoming.Enqueue(Event(facilityChannel, "payment.collected", Guid.NewGuid(), new { orderNumber = "RES-1", amount = "1500.0000", tender = "CARD_TERMINAL" }));
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(Info, deviceId, cts.Token, [facilityChannel]);
        await WaitUntilAsync(() => received.Count >= 1 && sent.Count >= 2);
        cts.Cancel();
        await run;

        Assert.Equal(2, sent.Count(m => m.Contains("pusher:subscribe", StringComparison.Ordinal)));
        Assert.Contains(sent, m => m.Contains(facilityChannel, StringComparison.Ordinal));
        var ev = Assert.Single(received);
        Assert.Equal("payment.collected", ev.Name);
        Assert.Equal(facilityChannel, ev.Channel);
        Assert.Equal("RES-1", ev.Data.GetProperty("orderNumber").GetString());
    }

    [Fact]
    public async Task ConnectionDrop_Reconnects_ReauthorisesWithTheNewSocketId_AndAsksConsumersToReload()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        var sockets = new Queue<ConcurrentQueue<string?>>();
        var first = new ConcurrentQueue<string?>();
        var second = new ConcurrentQueue<string?>();
        sockets.Enqueue(first);
        sockets.Enqueue(second);
        var sent = new List<string>();
        var connections = new List<Uri>();
        var subscribed = 0;
        var delays = new List<TimeSpan>();
        var client = new RealtimeClient(env.Api, () => new FakeSocket(sockets.Dequeue(), sent, connections), (d, _) =>
        {
            delays.Add(d);
            return Task.CompletedTask;
        });
        client.Subscribed += () => subscribed++;
        first.Enqueue(Established("1.1"));
        first.Enqueue("""{"event":"pusher_internal:subscription_succeeded","channel":"x","data":"{}"}""");
        first.Enqueue(null); // server closes
        second.Enqueue(Established("2.2"));
        second.Enqueue("""{"event":"pusher_internal:subscription_succeeded","channel":"x","data":"{}"}""");
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(Info, Guid.NewGuid(), cts.Token);
        await WaitUntilAsync(() => subscribed == 2);
        cts.Cancel();
        await run;

        Assert.Equal(2, connections.Count);
        Assert.Equal(2, env.Server.Requests.Count(r => r.Path == "/api/v1/broadcasting/auth")); // re-authorised: the socket id changed
        Assert.Contains("\"socket_id\":\"2.2\"", env.Server.Requests.Last(r => r.Path == "/api/v1/broadcasting/auth").Body, StringComparison.Ordinal);
        Assert.All(delays, d => Assert.InRange(d.TotalSeconds, 1d, 30d)); // capped exponential backoff with jitter
    }

    [Fact]
    public async Task AuthFailure_BacksOff_AndRetries_WithoutCrashing()
    {
        using var env = new TestEnv();
        await env.SignInAsync();
        env.Server.Offline = true; // the auth call fails
        var attempts = 0;
        var client = new RealtimeClient(env.Api, () =>
        {
            attempts++;
            var q = new ConcurrentQueue<string?>();
            q.Enqueue(Established("9.9"));
            return new FakeSocket(q, [], []);
        }, async (_, _) => await Task.Delay(1));
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(Info, Guid.NewGuid(), cts.Token);
        await WaitUntilAsync(() => attempts >= 3);
        cts.Cancel();
        await run;

        Assert.True(attempts >= 3);
    }

    // Shell wiring ------------------------------------------------------------------------------------------------
    [Fact]
    public async Task ApprovalDecidedPush_WakesTheWaitingDialogImmediately_WithoutWaitingForThePoll()
    {
        var incoming = new ConcurrentQueue<string?>();
        using var pos = new TestPos(
            new R007.Pos.Core.Configuration.PosOptions { Approvals = new() { PollIntervalSeconds = 3600, WaitTimeoutMinutes = 600 } },
            realtime: () => new FakeSocket(incoming, [], []));
        pos.Server.Realtime = Info;
        await pos.Ctx.RegisterAsync(new Uri("http://mock.local/"), "RESTAURANT", "T");
        var shell = new ShellViewModel(pos.Ctx, delay: (_, _) => Task.CompletedTask);
        await shell.StartAsync();                        // stored identity -> login; server info (with the Reverb endpoint) is read
        incoming.Enqueue(Established("7.7"));
        incoming.Enqueue("""{"event":"pusher_internal:subscription_succeeded","channel":"x","data":"{}"}""");

        await shell.HandleWedgeInputAsync(MockData.CashierNfc); // card tap -> PIN -> signed in -> the shell starts the realtime client
        var login = Assert.IsType<R007.Pos.ViewModels.Screens.LoginViewModel>(shell.Current);
        foreach (var c in MockData.CashierPin)
        {
            login.KeyCommand.Execute(c.ToString());
        }

        await login.SignInCommand.ExecuteAsync();
        await WaitUntilAsync(() => pos.Server.Requests.Any(r => r.Path == "/api/v1/broadcasting/auth"));

        var order = await pos.Env.Api.CreateOrderAsync(new CreateOrderRequest(pos.Ctx.FacilityId, Lines: [new OrderLineInput(MockData.Beer, 1)]), IdempotencyKeys.New());
        var approval = (await pos.Env.Api.VoidOrderAsync(order.Id, order.RowVersion, new VoidRequest("x"), IdempotencyKeys.New())).Pending!.Approval;
        // A wait whose own poll interval is an hour: only the push can finish it in time.
        var wait = pos.Ctx.Approvals.WaitForDecisionAsync(approval);
        pos.Server.DecideApprovalAsSupervisor(approval.Id, true);
        incoming.Enqueue(Event($"private-device.{pos.Ctx.Identity!.DeviceId:D}", "approval.decided", Guid.NewGuid(), new { approval = new { id = approval.Id }, applied = true }));

        var finished = await Task.WhenAny(wait, Task.Delay(3000));

        Assert.Same(wait, finished);
        Assert.True((await wait).Approved);
        await shell.SignOutAsync(); // stops the realtime loop
    }
}
