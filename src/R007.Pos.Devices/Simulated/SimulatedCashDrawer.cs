using R007.Pos.Devices.CashDrawer;

namespace R007.Pos.Devices.Simulated;

/// <summary>Cash drawer simulation; call <see cref="Close"/> to emulate the drawer being pushed shut.</summary>
public sealed class SimulatedCashDrawer : ICashDrawer
{
    private bool _isOpen;

    public int OpenCount { get; private set; }

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        _isOpen = true;
        OpenCount++;
        return Task.CompletedTask;
    }

    public Task<bool> IsOpenAsync(CancellationToken cancellationToken = default) => Task.FromResult(_isOpen);

    public void Close() => _isOpen = false;
}
