using PcHub.Platform;

namespace PcHub.Services;

/// <summary>
/// The user's own Anthropic API key, kept by the operating system's secret store: DPAPI on Windows (only this
/// Windows user can decrypt it), the Keychain on macOS. Never written in plain text or shown back in the UI.
/// A key found elsewhere on the PC (e.g. ANTHROPIC_API_KEY) is deliberately not used: it could belong to another
/// account, and scans are billed to whoever owns the key.
/// </summary>
public static class ClaudeKey
{
    public static string? Get(AppSettings s) => Os.Current.LoadSecret(s);

    /// <summary>Cheap check for the UI; doesn't read or decrypt the key.</summary>
    public static bool HasKey(AppSettings s) => Os.Current.HasSecret(s);

    /// <summary>Where the key is kept, in plain words, for the opt-in explanation.</summary>
    public static string WhereStored =>
        OperatingSystem.IsWindows() ? "stored encrypted for your Windows account"
        : OperatingSystem.IsMacOS() ? "stored in your macOS Keychain"
        : "stored in your desktop keyring, or in a file only your account can read";

    /// <summary>Saves the key (or removes it when null/blank). False if the secret store refused.</summary>
    public static bool Save(AppSettings s, string? key) => Os.Current.SaveSecret(s, key);
}
