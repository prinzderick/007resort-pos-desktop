namespace R007.Pos.Devices.CustomerDisplay;

/// <summary>Optional customer-facing pole/secondary display (typically 2 x 20 characters).</summary>
public interface ICustomerDisplay
{
    Task ShowAsync(string line1, string line2, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
