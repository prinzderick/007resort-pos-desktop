using System.Security.Cryptography;

namespace R007.Pos.Core.Security;

/// <summary>Small helpers to persist one protected blob atomically (temp file + replace).</summary>
public static class SecureFile
{
    public static void WriteProtected(string path, byte[] plaintext, IKeyProtector protector)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protector.Protect(plaintext));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Returns null if the file is missing or cannot be unprotected (different user/machine, corrupt).</summary>
    public static byte[]? ReadProtected(string path, IKeyProtector protector)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(File.ReadAllBytes(path));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
