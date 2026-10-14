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

/// <summary>
/// Every enrolment this terminal holds - one per server (the property server and the online server each issue their own
/// device token) - and which server is active. Shared by the file and in-memory stores so they behave the same.
/// </summary>
public sealed class IdentityBook
{
    public Uri? Active { get; set; }

    public List<DeviceIdentity> Identities { get; set; } = [];

    /// <summary>Servers are compared ignoring case and a trailing slash.</summary>
    public static string Key(Uri url) => url.AbsoluteUri.TrimEnd('/').ToLowerInvariant();

    public DeviceIdentity? ActiveIdentity() =>
        Active is null ? null : Identities.FirstOrDefault(i => Key(i.ServerUrl) == Key(Active));

    /// <summary>Stores (or replaces) the enrolment for its server and makes that server the active one.</summary>
    public void Upsert(DeviceIdentity identity)
    {
        Identities.RemoveAll(i => Key(i.ServerUrl) == Key(identity.ServerUrl));
        Identities.Add(identity);
        Active = identity.ServerUrl;
    }

    /// <summary>Forgets the active server's enrolment only; enrolments on other servers are kept.</summary>
    public void ForgetActive()
    {
        if (Active is not null)
        {
            Identities.RemoveAll(i => Key(i.ServerUrl) == Key(Active));
        }

        Active = null;
    }
}

public interface IDeviceIdentityStore
{
    /// <summary>The enrolment on the active server, or null when that server has none yet.</summary>
    DeviceIdentity? Load();

    /// <summary>Every enrolment held, one per server.</summary>
    IReadOnlyList<DeviceIdentity> All();

    /// <summary>Saves the enrolment for its server and makes that server active.</summary>
    void Save(DeviceIdentity identity);

    /// <summary>Makes <paramref name="serverUrl"/> the active server (it may have no enrolment yet).</summary>
    void Activate(Uri serverUrl);

    /// <summary>Forgets the active server's enrolment; enrolments on other servers are kept.</summary>
    void Clear();
}

/// <summary>Persists the device identities in a DPAPI-protected file (never in appsettings, never plain text).</summary>
public sealed class FileDeviceIdentityStore(string path, IKeyProtector protector) : IDeviceIdentityStore
{
    public DeviceIdentity? Load() => Read().ActiveIdentity();

    public IReadOnlyList<DeviceIdentity> All() => Read().Identities;

    public void Save(DeviceIdentity identity)
    {
        var book = Read();
        book.Upsert(identity);
        Write(book);
    }

    public void Activate(Uri serverUrl)
    {
        var book = Read();
        book.Active = serverUrl;
        Write(book);
    }

    public void Clear()
    {
        var book = Read();
        book.ForgetActive();
        if (book.Identities.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Write(book);
    }

    private IdentityBook Read()
    {
        var bytes = SecureFile.ReadProtected(path, protector);
        if (bytes is null)
        {
            return new IdentityBook();
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(nameof(IdentityBook.Identities), out _))
            {
                return doc.RootElement.Deserialize<IdentityBook>() ?? new IdentityBook();
            }

            // Older terminals stored one bare DeviceIdentity: keep it as the active enrolment.
            var legacy = doc.RootElement.Deserialize<DeviceIdentity>();
            return legacy is null ? new IdentityBook() : new IdentityBook { Active = legacy.ServerUrl, Identities = [legacy] };
        }
        catch (JsonException)
        {
            return new IdentityBook();
        }
    }

    private void Write(IdentityBook book) =>
        SecureFile.WriteProtected(path, JsonSerializer.SerializeToUtf8Bytes(book), protector);
}

public sealed class InMemoryDeviceIdentityStore(DeviceIdentity? initial = null) : IDeviceIdentityStore
{
    private readonly IdentityBook _book = Seed(initial);

    public DeviceIdentity? Load() => _book.ActiveIdentity();

    public IReadOnlyList<DeviceIdentity> All() => [.. _book.Identities];

    public void Save(DeviceIdentity identity) => _book.Upsert(identity);

    public void Activate(Uri serverUrl) => _book.Active = serverUrl;

    public void Clear() => _book.ForgetActive();

    private static IdentityBook Seed(DeviceIdentity? initial)
    {
        var book = new IdentityBook();
        if (initial is not null)
        {
            book.Upsert(initial);
        }

        return book;
    }
}
