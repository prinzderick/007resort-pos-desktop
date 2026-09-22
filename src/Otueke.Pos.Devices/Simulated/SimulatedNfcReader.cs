using Otueke.Pos.Devices.Nfc;

namespace Otueke.Pos.Devices.Simulated;

/// <summary>NFC reader driven from code (dev tools / tests) via <see cref="SimulateTap"/>.</summary>
public sealed class SimulatedNfcReader(TimeProvider timeProvider) : INfcReader
{
    public SimulatedNfcReader()
        : this(TimeProvider.System)
    {
    }

    public event EventHandler<NfcTagReadEventArgs>? TagRead;

    public bool IsListening { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IsListening = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsListening = false;
        return Task.CompletedTask;
    }

    /// <summary>Raises <see cref="TagRead"/> if the reader is listening. Returns whether it was raised.</summary>
    public bool SimulateTap(string uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        if (!IsListening)
        {
            return false;
        }

        TagRead?.Invoke(this, new NfcTagReadEventArgs(uid.ToUpperInvariant(), timeProvider.GetUtcNow()));
        return true;
    }
}
