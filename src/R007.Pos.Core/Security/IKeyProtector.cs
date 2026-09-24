using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace R007.Pos.Core.Security;

/// <summary>Wraps small secrets (keys, tokens) with an OS-bound protector so they are unreadable off this machine/user.</summary>
public interface IKeyProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Windows DPAPI (current user scope) protector. Only usable on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : IKeyProtector
{
    private static readonly byte[] Entropy = "R007.Pos.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// NOT secure: reversible with no OS binding. Only for tests, the mock demo mode and non-Windows dev machines.
/// The composition root must never select it when talking to a real backend.
/// </summary>
public sealed class InsecureKeyProtector : IKeyProtector
{
    private static readonly byte[] Magic = "R007-INSECURE"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => [.. Magic, .. plaintext];

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < Magic.Length || !protectedData.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Not protected by InsecureKeyProtector.");
        }

        return protectedData[Magic.Length..];
    }
}
