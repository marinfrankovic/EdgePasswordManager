namespace EdgePasswordBulkManager.Models;

/// <summary>
/// A single saved-login record read from the Chromium "logins" table.
/// Only metadata is represented — password_value is never decrypted or stored.
/// </summary>
public sealed class LoginEntry
{
    /// <summary>
    /// Row id from the "id" column when the schema exposes one (modern Chromium).
    /// Null when the schema uses the legacy composite primary key.
    /// </summary>
    public long? Id { get; init; }

    public string OriginUrl { get; init; } = string.Empty;
    public string ActionUrl { get; init; } = string.Empty;
    public string SignonRealm { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;

    // Fields required to build a stable composite WHERE clause when Id is unavailable.
    public string UsernameElement { get; init; } = string.Empty;
    public string PasswordElement { get; init; } = string.Empty;

    public DateTimeOffset? DateCreated { get; init; }
    public DateTimeOffset? DateLastUsed { get; init; }
    public int TimesUsed { get; init; }
    public bool Blacklisted { get; init; }

    /// <summary>Registrable-ish domain used for duplicate grouping and category matching.</summary>
    public string NormalizedDomain { get; init; } = string.Empty;

    /// <summary>Which profile this entry came from (needed for cross-profile aggregate + delete routing).</summary>
    public string ProfileKey { get; set; } = string.Empty;
    public string ProfileLabel { get; set; } = string.Empty;

    /// <summary>UI-only selection flag.</summary>
    public bool Selected { get; set; }

    /// <summary>UI-only duplicate flag (same normalized domain + username as another row).</summary>
    public bool IsDuplicate { get; set; }

    /// <summary>Category names matched from loaded blocklists (e.g. "adult", "gambling").</summary>
    public List<string> Categories { get; set; } = new();

    public bool IsAdult => Categories.Contains("adult");

    /// <summary>True when the saved origin/realm uses insecure HTTP.</summary>
    public bool IsInsecure =>
        OriginUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        SignonRealm.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public bool NeverUsed => TimesUsed == 0;

    /// <summary>Stable per-row key (profile-scoped) for Blazor @key and result correlation.</summary>
    public string RowKey => $"{ProfileKey}\u0001" + (Id?.ToString()
        ?? $"{OriginUrl}\u0001{UsernameElement}\u0001{Username}\u0001{PasswordElement}\u0001{SignonRealm}");
}
