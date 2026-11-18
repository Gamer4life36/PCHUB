using System.Security.Cryptography;
using System.Text;

namespace PcHub.Services;

/// <summary>
/// The user's own Anthropic API key, encrypted with Windows DPAPI (only this Windows user on this PC can
/// decrypt it). It is never written to disk in plain text or shown back in the UI.
/// </summary>
public static class ClaudeKey
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PCHUB.ClaudeKey.v1");

    /// <summary>Encrypts the key, or returns null if Windows can't (unusual profiles).</summary>
    public static string? Protect(string key)
    {
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// The key the user saved in PCHUB, or null. An ANTHROPIC_API_KEY on the PC is deliberately not used:
    /// it could belong to another account, and checks are billed to whoever owns the key.
    /// </summary>
    public static string? Get(AppSettings s)
    {
        if (!string.IsNullOrEmpty(s.ClaudeKeyProtected))
        {
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s.ClaudeKeyProtected), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Saved under a different Windows user or damaged: treat as not set.
            }
        }

        return null;
    }

    public static bool HasKey(AppSettings s) => Get(s) is not null;
}
