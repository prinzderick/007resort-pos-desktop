using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using R007.Pos.Core.Security;

namespace R007.Pos.Core.Offline;

/// <summary>
/// File-backed <see cref="IOfflineQueue"/>. Layout: a sequence of records <c>[u32 length][12B nonce][16B tag][ciphertext]</c>,
/// each AES-256-GCM encrypted with a per-device key that is itself stored DPAPI-protected next to the queue.
/// The record's sequence number is bound into the GCM associated data, so deleting, reordering or splicing records
/// is detected. Records are only ever appended (an op, then later a "done"/"rejected"/"ack" record referencing it).
/// A torn final record (power loss mid-write) is discarded; any other authentication failure keeps the valid
/// prefix, preserves the original file as <c>.corrupt</c> and raises <see cref="QueueStatus.CorruptionDetected"/>.
/// When nothing is pending or awaiting acknowledgement the file is rotated (deleted) to bound its size.
/// </summary>
public sealed class EncryptedFileOfflineQueue : IOfflineQueue, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int MaxRecordSize = 4 * 1024 * 1024;

    private readonly string _path;
    private readonly string _keyPath;
    private readonly IKeyProtector _protector;
    private readonly TimeProvider _time;
    private readonly OfflineQueueOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly List<QueuedOperation> _ops = [];
    private readonly Dictionary<Guid, string?> _resolved = []; // key -> rejection reason (null = replayed)
    private readonly Dictionary<Guid, DateTimeOffset> _rejectedAt = [];
    private readonly HashSet<Guid> _acked = [];
    private byte[]? _key;
    private long _sequence;
    private bool _loaded;
    private bool _corrupt;

    public EncryptedFileOfflineQueue(string path, IKeyProtector protector, TimeProvider time, OfflineQueueOptions? options = null)
    {
        _path = path;
        _keyPath = path + ".key";
        _protector = protector;
        _time = time;
        _options = options ?? new OfflineQueueOptions();
    }

    public async Task EnqueueAsync(QueuedOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            if (_ops.Any(o => o.IdempotencyKey == operation.IdempotencyKey))
            {
                return;
            }

            var status = ComputeStatus();
            if (status.IsBlocked)
            {
                throw new OfflineQueueBlockedException(status.BlockedReason ?? "The emergency queue is not accepting new operations.");
            }

            Append(new QueueRecord("op", operation, null, null, _time.GetUtcNow()));
            _ops.Add(operation);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<QueuedOperation>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return Pending();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkReplayedAsync(Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        ResolveAsync(new QueueRecord("done", null, idempotencyKey, null, _time.GetUtcNow()), cancellationToken);

    public Task MarkRejectedAsync(Guid idempotencyKey, string reason, CancellationToken cancellationToken = default) =>
        ResolveAsync(new QueueRecord("rej", null, idempotencyKey, reason, _time.GetUtcNow()), cancellationToken);

    public Task AcknowledgeRejectedAsync(Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        ResolveAsync(new QueueRecord("ack", null, idempotencyKey, null, _time.GetUtcNow()), cancellationToken);

    public async Task<IReadOnlyList<RejectedOperation>> GetRejectedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return
            [
                .. _ops.Where(o => _resolved.TryGetValue(o.IdempotencyKey, out var r) && r is not null && !_acked.Contains(o.IdempotencyKey))
                    .Select(o => new RejectedOperation(o, _resolved[o.IdempotencyKey]!, _rejectedAt[o.IdempotencyKey])),
            ];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return Pending().Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QueueStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            return ComputeStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task ResolveAsync(QueueRecord record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureLoaded();
            var key = record.Key!.Value;
            if (!_ops.Any(o => o.IdempotencyKey == key))
            {
                throw new InvalidOperationException("Unknown queued operation.");
            }

            if (record.K != "ack" && _resolved.ContainsKey(key))
            {
                return; // already resolved; idempotent
            }

            Append(record);
            Apply(record);
            Rotate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<QueuedOperation> Pending() => [.. _ops.Where(o => !_resolved.ContainsKey(o.IdempotencyKey))];

    private QueueStatus ComputeStatus()
    {
        var pending = Pending();
        TimeSpan? oldest = pending.Count == 0 ? null : _time.GetUtcNow() - pending.Min(o => o.CreatedAtUtc);
        string? reason = null;
        if (pending.Count >= _options.MaxEntries)
        {
            reason = $"The emergency queue is full ({pending.Count} operations). Reconnect to confirm pending transactions.";
        }
        else if (oldest is { } age && age > _options.MaxAge)
        {
            reason = $"Pending operations are older than {_options.MaxAge.TotalMinutes:0} minutes. Reconnect to confirm pending transactions.";
        }

        var rejected = _ops.Count(o => _resolved.TryGetValue(o.IdempotencyKey, out var r) && r is not null && !_acked.Contains(o.IdempotencyKey));
        return new QueueStatus(pending.Count, oldest, reason is not null, reason, rejected, _corrupt);
    }

    private void Apply(QueueRecord r)
    {
        switch (r.K)
        {
            case "op" when r.Op is not null:
                _ops.Add(r.Op);
                break;
            case "done" when r.Key is { } k:
                _resolved[k] = null;
                break;
            case "rej" when r.Key is { } k:
                _resolved[k] = r.Reason ?? "Rejected";
                _rejectedAt[k] = r.At;
                break;
            case "ack" when r.Key is { } k:
                _acked.Add(k);
                break;
            default:
                break;
        }
    }

    /// <summary>Everything resolved and acknowledged: drop the file (rotation) so it stays small.</summary>
    private void Rotate()
    {
        var unfinished = _ops.Any(o => !_resolved.ContainsKey(o.IdempotencyKey)
            || (_resolved[o.IdempotencyKey] is not null && !_acked.Contains(o.IdempotencyKey)));
        if (unfinished)
        {
            return;
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        _ops.Clear();
        _resolved.Clear();
        _rejectedAt.Clear();
        _acked.Clear();
        _sequence = 0;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _key = LoadOrCreateKey();
        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(_path);
        var offset = 0;
        var validEnd = 0;
        var damaged = false;
        var records = new List<QueueRecord>();

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 4)
            {
                damaged = true; // torn length prefix (crash mid-write): discard silently
                break;
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            if (length < NonceSize + TagSize || length > MaxRecordSize)
            {
                damaged = true;
                _corrupt = true; // implausible length inside the file: not a torn write
                break;
            }

            if (offset + 4 + length > bytes.Length)
            {
                damaged = true; // torn final record: discard silently
                break;
            }

            var record = TryDecrypt(bytes.AsSpan(offset + 4, length), records.Count);
            if (record is null)
            {
                damaged = true;
                _corrupt = true; // fully present but fails authentication: tampering or corruption
                break;
            }

            records.Add(record);
            offset += 4 + length;
            validEnd = offset;
        }

        foreach (var r in records)
        {
            Apply(r);
        }

        _sequence = records.Count;

        if (damaged)
        {
            if (_corrupt)
            {
                File.Copy(_path, _path + ".corrupt", overwrite: true);
            }

            File.WriteAllBytes(_path, bytes.AsSpan(0, validEnd).ToArray());
        }
    }

    private byte[] LoadOrCreateKey()
    {
        var existing = SecureFile.ReadProtected(_keyPath, _protector);
        if (existing is { Length: KeySize })
        {
            return existing;
        }

        if (File.Exists(_path))
        {
            // The queue file exists but its key is gone/unreadable: we can never decrypt it.
            File.Move(_path, _path + ".unreadable", overwrite: true);
            _corrupt = true;
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        SecureFile.WriteProtected(_keyPath, key, _protector);
        return key;
    }

    private void Append(QueueRecord record)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(record, QueueJsonContext.Default.QueueRecord);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[json.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(_key!, TagSize))
        {
            aes.Encrypt(nonce, json, cipher, tag, Aad(_sequence));
        }

        var length = NonceSize + TagSize + cipher.Length;
        var buffer = new byte[4 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)length);
        nonce.CopyTo(buffer, 4);
        tag.CopyTo(buffer, 4 + NonceSize);
        cipher.CopyTo(buffer, 4 + NonceSize + TagSize);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            fs.Write(buffer);
            fs.Flush(flushToDisk: true);
        }

        _sequence++;
    }

    private QueueRecord? TryDecrypt(ReadOnlySpan<byte> block, long sequence)
    {
        var nonce = block[..NonceSize];
        var tag = block.Slice(NonceSize, TagSize);
        var cipher = block[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(_key!, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Aad(sequence));
            return JsonSerializer.Deserialize(plain, QueueJsonContext.Default.QueueRecord);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static byte[] Aad(long sequence)
    {
        var aad = new byte[8 + 6];
        "R007Q1"u8.CopyTo(aad);
        BinaryPrimitives.WriteInt64LittleEndian(aad.AsSpan(6), sequence);
        return aad;
    }
}

internal sealed record QueueRecord(string K, QueuedOperation? Op, Guid? Key, string? Reason, DateTimeOffset At);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QueueRecord))]
internal sealed partial class QueueJsonContext : JsonSerializerContext
{
}
