using R007.Pos.Core.Api;

namespace R007.Pos.Core.Http;

/// <summary>
/// Holds the current credentials the HTTP pipeline attaches: the device token (identifies this registered terminal)
/// and the signed-in staff member's access/refresh tokens. In-memory only; the device token is persisted separately
/// (protected) by <c>IDeviceIdentityStore</c>; staff tokens are never written to disk.
/// </summary>
public sealed class AuthState
{
    private readonly object _gate = new();

    public event EventHandler? Changed;

    /// <summary>Raised when the refresh token was rejected: the staff member must sign in again.</summary>
    public event EventHandler? SessionExpired;

    public string? DeviceToken { get; private set; }

    public string? AccessToken { get; private set; }

    public string? RefreshToken { get; private set; }

    public DateTimeOffset? AccessExpiresAt { get; private set; }

    public Staff? Staff { get; private set; }

    public bool IsSignedIn => AccessToken is not null && Staff is not null;

    public void SetDeviceToken(string? token)
    {
        lock (_gate)
        {
            DeviceToken = token;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignIn(AuthResult login, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(login);
        lock (_gate)
        {
            AccessToken = login.AccessToken;
            RefreshToken = login.RefreshToken;
            AccessExpiresAt = now.AddSeconds(login.ExpiresInSeconds);
            Staff = login.Staff;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Applies a refreshed token pair, keeping the existing staff info if the response omitted it.</summary>
    public void UpdateTokens(AuthResult refreshed, DateTimeOffset now)
    {
        lock (_gate)
        {
            AccessToken = refreshed.AccessToken;
            RefreshToken = refreshed.RefreshToken;
            AccessExpiresAt = now.AddSeconds(refreshed.ExpiresInSeconds);
            Staff = refreshed.Staff ?? Staff;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        lock (_gate)
        {
            AccessToken = null;
            RefreshToken = null;
            AccessExpiresAt = null;
            Staff = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ExpireSession()
    {
        SignOut();
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }
}
