namespace R007.Pos.Devices.Nfc;

/// <summary>
/// NFC card/tag reader (staff badges at fixed stations, membership cards). The raw UID is passed to
/// the API for resolution; it must never be logged in clear or used for local authorization.
/// </summary>
public interface INfcReader
{
    event EventHandler<NfcTagReadEventArgs>? TagRead;

    bool IsListening { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed class NfcTagReadEventArgs(string uid, DateTimeOffset readAtUtc) : EventArgs
{
    /// <summary>Tag UID as an uppercase hex string. Treat as sensitive.</summary>
    public string Uid { get; } = uid;

    public DateTimeOffset ReadAtUtc { get; } = readAtUtc;
}
