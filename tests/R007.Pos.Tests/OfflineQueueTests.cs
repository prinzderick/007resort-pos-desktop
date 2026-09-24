using System.Security.Cryptography;
using System.Text;
using R007.Pos.Core.Offline;
using R007.Pos.Core.Security;

namespace R007.Pos.Tests;

public sealed class OfflineQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r007-queue-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public OfflineQueueTests() => Directory.CreateDirectory(_dir);

    private string QueuePath => Path.Combine(_dir, "queue.bin");

    private EncryptedFileOfflineQueue Open(OfflineQueueOptions? options = null, IKeyProtector? protector = null) =>
        new(QueuePath, protector ?? new InsecureKeyProtector(), _time, options);

    private QueuedOperation Op(string body = """{"secret":"CARD-1234-5678"}""", string? description = "test") =>
        new(Guid.CreateVersion7(), "POST", "api/v1/payments", body, _time.GetUtcNow(), Guid.NewGuid(), description);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task FileOnDisk_ContainsNoPlaintext()
    {
        using var q = Open();
        await q.EnqueueAsync(Op("""{"amount":"5000.0000","reference":"PLAINTEXT-MARKER"}""", "Cash 5000 for order ORD-1"));

        var bytes = await File.ReadAllBytesAsync(QueuePath);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("PLAINTEXT-MARKER", text, StringComparison.Ordinal);
        Assert.DoesNotContain("5000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("api/v1/payments", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ORD-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Key_IsStoredProtected_NotInTheClear()
    {
        using var q = Open(protector: new InsecureKeyProtector());
        await q.EnqueueAsync(Op());

        var stored = await File.ReadAllBytesAsync(QueuePath + ".key");
        Assert.Equal("R007-INSECURE"u8.ToArray(), stored[..13]); // wrapped by the protector (DPAPI in production)
        Assert.Equal(32, stored.Length - 13);
    }

    [Fact]
    public async Task Pending_IsReturnedInCaptureOrder_AndSurvivesRestart()
    {
        var a = Op(description: "first");
        var b = Op(description: "second");
        var c = Op(description: "third");
        using (var q = Open())
        {
            await q.EnqueueAsync(a);
            await q.EnqueueAsync(b);
            await q.EnqueueAsync(c);
        }

        using var reopened = Open();
        var pending = await reopened.GetPendingAsync();

        Assert.Equal([a.IdempotencyKey, b.IdempotencyKey, c.IdempotencyKey], pending.Select(p => p.IdempotencyKey));
        Assert.Equal(a.JsonBody, pending[0].JsonBody);
    }

    [Fact]
    public async Task Enqueue_IsIdempotentOnTheKey()
    {
        using var q = Open();
        var op = Op();

        await q.EnqueueAsync(op);
        await q.EnqueueAsync(op);

        Assert.Equal(1, await q.CountPendingAsync());
    }

    [Fact]
    public async Task MarkReplayed_RemovesFromPending_AndPersists()
    {
        var a = Op();
        var b = Op();
        using (var q = Open())
        {
            await q.EnqueueAsync(a);
            await q.EnqueueAsync(b);
            await q.MarkReplayedAsync(a.IdempotencyKey);
        }

        using var reopened = Open();
        Assert.Equal([b.IdempotencyKey], (await reopened.GetPendingAsync()).Select(p => p.IdempotencyKey));
    }

    [Fact]
    public async Task File_IsAppendOnly_ExistingBytesNeverChange()
    {
        using var q = Open();
        var a = Op();
        await q.EnqueueAsync(a);
        var before = await File.ReadAllBytesAsync(QueuePath);

        await q.EnqueueAsync(Op());
        await q.EnqueueAsync(Op());
        await q.MarkReplayedAsync(a.IdempotencyKey);
        var after = await File.ReadAllBytesAsync(QueuePath);

        Assert.True(after.Length > before.Length);
        Assert.Equal(before, after[..before.Length]);
    }

    [Fact]
    public async Task FullyResolvedQueue_RotatesTheFile()
    {
        using var q = Open();
        var a = Op();
        await q.EnqueueAsync(a);
        Assert.True(File.Exists(QueuePath));

        await q.MarkReplayedAsync(a.IdempotencyKey);

        Assert.False(File.Exists(QueuePath));
        Assert.Equal(0, await q.CountPendingAsync());
    }

    [Fact]
    public async Task Rejected_StaysVisibleUntilAcknowledged_NeverDroppedSilently()
    {
        var a = Op(description: "Cash 5000");
        using (var q = Open())
        {
            await q.EnqueueAsync(a);
            await q.MarkRejectedAsync(a.IdempotencyKey, "balance_changed: order already paid");
            Assert.Equal(0, await q.CountPendingAsync());
            Assert.True(File.Exists(QueuePath)); // kept: not acknowledged yet
        }

        using var reopened = Open();
        var rejected = Assert.Single(await reopened.GetRejectedAsync());
        Assert.Equal("balance_changed: order already paid", rejected.Reason);
        Assert.Equal(1, (await reopened.GetStatusAsync()).UnacknowledgedRejectedCount);

        await reopened.AcknowledgeRejectedAsync(a.IdempotencyKey);

        Assert.Empty(await reopened.GetRejectedAsync());
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public async Task Bounded_ByCount_BlocksNewOperations()
    {
        using var q = Open(new OfflineQueueOptions { MaxEntries = 2, MaxAge = TimeSpan.FromHours(1) });
        await q.EnqueueAsync(Op());
        await q.EnqueueAsync(Op());

        var ex = await Assert.ThrowsAsync<OfflineQueueBlockedException>(() => q.EnqueueAsync(Op()));

        Assert.Contains("full", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True((await q.GetStatusAsync()).IsBlocked);
    }

    [Fact]
    public async Task Bounded_ByAge_BlocksNewOperations_UntilReplayed()
    {
        using var q = Open(new OfflineQueueOptions { MaxEntries = 100, MaxAge = TimeSpan.FromMinutes(30) });
        var old = Op();
        await q.EnqueueAsync(old);
        _time.Advance(TimeSpan.FromMinutes(31));

        await Assert.ThrowsAsync<OfflineQueueBlockedException>(() => q.EnqueueAsync(Op()));

        await q.MarkReplayedAsync(old.IdempotencyKey);
        await q.EnqueueAsync(Op()); // accepted again once the stale one is resolved
        Assert.Equal(1, await q.CountPendingAsync());
    }

    [Fact]
    public async Task TamperedRecord_IsDetected_ValidPrefixKept_OriginalPreserved()
    {
        var a = Op(description: "keep");
        var b = Op(description: "tampered");
        using (var q = Open())
        {
            await q.EnqueueAsync(a);
            await q.EnqueueAsync(b);
        }

        var bytes = await File.ReadAllBytesAsync(QueuePath);
        bytes[^5] ^= 0xFF; // flip a bit inside the last record's ciphertext
        await File.WriteAllBytesAsync(QueuePath, bytes);

        using var reopened = Open();
        var status = await reopened.GetStatusAsync();

        Assert.True(status.CorruptionDetected);
        Assert.Equal([a.IdempotencyKey], (await reopened.GetPendingAsync()).Select(p => p.IdempotencyKey));
        Assert.True(File.Exists(QueuePath + ".corrupt"));
    }

    [Fact]
    public async Task DeletedMiddleRecord_IsDetected_ByBoundSequenceNumbers()
    {
        var ops = new[] { Op(), Op(), Op() };
        using (var q = Open())
        {
            foreach (var op in ops)
            {
                await q.EnqueueAsync(op);
            }
        }

        // Remove the first record entirely (splice): remaining records no longer authenticate at their new positions.
        var bytes = await File.ReadAllBytesAsync(QueuePath);
        var firstLength = (int)BitConverter.ToUInt32(bytes, 0) + 4;
        await File.WriteAllBytesAsync(QueuePath, bytes[firstLength..]);

        using var reopened = Open();

        Assert.True((await reopened.GetStatusAsync()).CorruptionDetected);
        Assert.Empty(await reopened.GetPendingAsync());
    }

    [Fact]
    public async Task TornFinalWrite_IsDiscardedSilently_NotTreatedAsTampering()
    {
        var a = Op();
        using (var q = Open())
        {
            await q.EnqueueAsync(a);
            await q.EnqueueAsync(Op());
        }

        var bytes = await File.ReadAllBytesAsync(QueuePath);
        await File.WriteAllBytesAsync(QueuePath, bytes[..^7]); // power loss mid-write

        using var reopened = Open();

        Assert.False((await reopened.GetStatusAsync()).CorruptionDetected);
        Assert.Equal([a.IdempotencyKey], (await reopened.GetPendingAsync()).Select(p => p.IdempotencyKey));
        await reopened.EnqueueAsync(Op()); // and it can keep appending after repair
        Assert.Equal(2, await reopened.CountPendingAsync());
    }

    [Fact]
    public async Task WrongKey_MakesFileUnreadable_NotPlaintext()
    {
        var op = Op();
        using (var q = Open())
        {
            await q.EnqueueAsync(op);
        }

        File.Delete(QueuePath + ".key"); // key lost (different Windows user / machine)
        using var reopened = Open();

        Assert.Equal(0, await reopened.CountPendingAsync());
        Assert.True((await reopened.GetStatusAsync()).CorruptionDetected);
        Assert.True(File.Exists(QueuePath + ".unreadable"));
    }

    [Fact]
    public async Task ConcurrentEnqueues_DoNotLoseOrCorruptRecords()
    {
        using var q = Open(new OfflineQueueOptions { MaxEntries = 500 });
        var ops = Enumerable.Range(0, 50).Select(_ => Op()).ToList();

        await Task.WhenAll(ops.Select(o => Task.Run(() => q.EnqueueAsync(o))));

        using var reopened = Open(new OfflineQueueOptions { MaxEntries = 500 });
        var pending = await reopened.GetPendingAsync();
        Assert.Equal(50, pending.Count);
        Assert.Equal(ops.Select(o => o.IdempotencyKey).Order(), pending.Select(p => p.IdempotencyKey).Order());
    }

    [Fact]
    public void InsecureProtector_RejectsForeignData()
    {
        Assert.Throws<CryptographicException>(() => new InsecureKeyProtector().Unprotect("not protected"u8.ToArray()));
    }
}
