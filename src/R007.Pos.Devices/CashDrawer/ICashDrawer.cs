namespace R007.Pos.Devices.CashDrawer;

/// <summary>
/// Cash drawer (usually kicked via the receipt printer). Opening the drawer is a sensitive action:
/// the POS must obtain authorization from the API (which audits it) before calling <see cref="OpenAsync"/>.
/// </summary>
public interface ICashDrawer
{
    Task OpenAsync(CancellationToken cancellationToken = default);

    Task<bool> IsOpenAsync(CancellationToken cancellationToken = default);
}
