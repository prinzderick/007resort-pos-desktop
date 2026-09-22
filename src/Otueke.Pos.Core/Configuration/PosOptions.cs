namespace Otueke.Pos.Core.Configuration;

/// <summary>Non-secret POS configuration bound from <c>appsettings.json</c> / environment variables.</summary>
public sealed class PosOptions
{
    public const string SectionName = "Pos";

    /// <summary>Base URL of the on-site Otueke API (e.g. <c>http://localhost:5080</c>).</summary>
    public Uri ApiBaseUrl { get; set; } = new("http://localhost:5080");

    public DeviceRegistrationOptions DeviceRegistration { get; set; } = new();
}

/// <summary>
/// Device registration settings. Device credentials issued by the API are stored in the OS-protected
/// store (e.g. DPAPI / Windows Credential Manager), never in configuration files.
/// </summary>
public sealed class DeviceRegistrationOptions
{
    /// <summary>Human-friendly name suggested at registration (e.g. "Main Reception 1").</summary>
    public string? DeviceName { get; set; }

    /// <summary>Registered device id once enrolled; empty until the device is registered by an administrator.</summary>
    public Guid? DeviceId { get; set; }
}
