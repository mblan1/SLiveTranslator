using System;
using System.Security.Cryptography;
using System.Text;

namespace SLiveTranslator.Models;

/// <summary>
/// Encrypts API keys with Windows DPAPI for the current Windows user. A blob copied to another user or PC
/// cannot be decrypted; <see cref="Unprotect"/> then returns null and the key has to be entered again.
/// </summary>
public static class SecretStore
{
    static readonly byte[] Entropy = "SLiveTranslator/api-keys/v1"u8.ToArray();

    /// <summary>Base64 DPAPI blob of <paramref name="secret"/>; "" for an empty secret.</summary>
    public static string Protect(string secret) =>
        string.IsNullOrEmpty(secret)
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>The secret; "" when nothing is stored; null when the blob is damaged or belongs to another user or PC.</summary>
    public static string? Unprotect(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
