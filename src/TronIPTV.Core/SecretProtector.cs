using System.Security.Cryptography;
using System.Text;

namespace TronIPTV.Core;

/// <summary>Protects stored secrets with Windows DPAPI, scoped to the current user.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = "TronIPTV.v1"u8.ToArray();

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return null;
        if (!OperatingSystem.IsWindows())
            return "plain:" + plain;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static byte[] ProtectBytes(byte[] plain) =>
        OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser) : plain;

    /// <summary>Returns null when the data was protected for another user or machine.</summary>
    public static byte[]? UnprotectBytes(byte[] stored)
    {
        if (!OperatingSystem.IsWindows())
            return stored;
        try
        {
            return ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        if (stored.StartsWith("plain:", StringComparison.Ordinal))
            return stored["plain:".Length..];
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Settings copied from another user or machine cannot be decrypted; the user must re-enter the secret.
            return null;
        }
    }
}
