namespace R007.Pos.Core.Api;

/// <summary>
/// Typed client for the 007 Resort & Spa API. The POS is a thin client: every business decision (pricing,
/// tax, permissions, stock, entitlements) is made by the API — never re-implemented here.
/// </summary>
public interface IR007ApiClient
{
    /// <summary>Calls <c>GET /api/v1/system/info</c>.</summary>
    Task<SystemInfo> GetSystemInfoAsync(CancellationToken cancellationToken = default);
}
