using Otueke.Pos.Devices.CustomerDisplay;

namespace Otueke.Pos.Devices.Simulated;

/// <summary>Customer display simulation exposing the current text.</summary>
public sealed class SimulatedCustomerDisplay : ICustomerDisplay
{
    public string Line1 { get; private set; } = string.Empty;

    public string Line2 { get; private set; } = string.Empty;

    public Task ShowAsync(string line1, string line2, CancellationToken cancellationToken = default)
    {
        Line1 = line1 ?? string.Empty;
        Line2 = line2 ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => ShowAsync(string.Empty, string.Empty, cancellationToken);
}
