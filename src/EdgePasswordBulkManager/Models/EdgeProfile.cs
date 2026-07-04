namespace EdgePasswordBulkManager.Models;

/// <summary>
/// A discovered Microsoft Edge profile that contains a Chromium "Login Data" database.
/// </summary>
public sealed class EdgeProfile
{
    /// <summary>Channel label, e.g. "Stable", "Beta", "Dev".</summary>
    public required string Channel { get; init; }

    /// <summary>On-disk folder name inside the User Data directory, e.g. "Default", "Profile 1".</summary>
    public required string FolderName { get; init; }

    /// <summary>Friendly name resolved from Local State (profile.info_cache), falls back to folder name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Absolute path to the "Login Data" SQLite file for this profile.</summary>
    public required string LoginDataPath { get; init; }

    /// <summary>Number of saved logins (populated lazily after a load).</summary>
    public int EntryCount { get; set; }

    /// <summary>Stable identifier used by the UI selector.</summary>
    public string Key => $"{Channel}|{FolderName}";

    public string Label => $"{DisplayName}  ({Channel} / {FolderName})";
}
