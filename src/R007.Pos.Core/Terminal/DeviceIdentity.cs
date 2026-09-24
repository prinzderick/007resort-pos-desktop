using System.Text.Json;
using R007.Pos.Core.Security;

namespace R007.Pos.Core.Terminal;

/// <summary>What enrolment gave this terminal. The <see cref="DeviceToken"/> is a secret: only stored protected.</summary>
public sealed record DeviceIdentity(
    Uri ServerUrl,
    Guid DeviceId,
    string DeviceToken,
    string Name,
    Guid FacilityUnitId,
    Guid? OperatingPointId,
    string? FacilityName);

public interface IDeviceIdentityStore
{
    DeviceIdentity? Load();

    void Save(DeviceIdentity identity);

    void Clear();
}

/// <summary>Persists the device identity in a DPAPI-protected file (never in appsettings, never plain text).</summary>
public sealed class FileDeviceIdentityStore(string path, IKeyProtector protector) : IDeviceIdentityStore
{
    public DeviceIdentity? Load()
    {
        var bytes = SecureFile.ReadProtected(path, protector);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceIdentity>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(DeviceIdentity identity) =>
        SecureFile.WriteProtected(path, JsonSerializer.SerializeToUtf8Bytes(identity), protector);

    public void Clear()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

public sealed class InMemoryDeviceIdentityStore(DeviceIdentity? initial = null) : IDeviceIdentityStore
{
    private DeviceIdentity? _identity = initial;

    public DeviceIdentity? Load() => _identity;

    public void Save(DeviceIdentity identity) => _identity = identity;

    public void Clear() => _identity = null;
}
