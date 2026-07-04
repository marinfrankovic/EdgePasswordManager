namespace EdgePasswordBulkManager.Models;

/// <summary>
/// A discovered Microsoft Edge login store — one Chromium "logins" SQLite file inside a
/// profile. A single profile can expose several stores (e.g. "Login Data", "Login Data New",
/// "Login Data For Account"); recent Edge builds keep the live data in "Login Data New".
/// </summary>
public sealed class EdgeProfile
{
    /// <summary>Channel label, e.g. "Stable", "Beta", "Dev".</summary>
    public required string Channel { get; init; }

    /// <summary>On-disk folder name inside the User Data directory, e.g. "Default", "Profile 1".</summary>
    public required string FolderName { get; init; }

    /// <summary>Friendly profile name resolved from Local State (profile.info_cache).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The store file name, e.g. "Login Data", "Login Data New", "Login Data For Account".</summary>
    public required string StoreFile { get; init; }

    /// <summary>Absolute path to this store's SQLite file.</summary>
    public required string LoginDataPath { get; init; }

    /// <summary>Last write time of the store file (helps identify the active store).</summary>
    public DateTimeOffset LastModified { get; init; }

    /// <summary>Number of saved logins (populated lazily after a load).</summary>
    public int EntryCount { get; set; }

    /// <summary>True for the Microsoft-account (synced) store variants.</summary>
    public bool IsAccountStore => StoreFile.Contains("For Account", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stable identifier used by the UI selector and delete routing.</summary>
    public string Key => $"{Channel}|{FolderName}|{StoreFile}";

    /// <summary>Groups stores that belong to the same profile.</summary>
    public string ProfileGroupKey => $"{Channel}|{FolderName}";

    public string ProfileHeader => $"{DisplayName} ({Channel} / {FolderName})";

    public string Label => $"{DisplayName} — {StoreFile}";
}
