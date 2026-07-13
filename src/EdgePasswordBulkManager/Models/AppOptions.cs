namespace EdgePasswordBulkManager.Models;

/// <summary>Bound configuration for where Edge data lives and where the app writes its artifacts.</summary>
public sealed class AppOptions
{
    public const string SectionName = "EdgePassManager";

    /// <summary>
    /// One or more Edge "User Data" root directories to scan (Stable/Beta/Dev).
    /// Inside Docker these are bind-mount targets such as /edge-data.
    /// </summary>
    public List<EdgeDataRoot> EdgeDataRoots { get; set; } = new();

    /// <summary>Directory for timestamped backups of the Login Data DB before deletes.</summary>
    public string BackupPath { get; set; } = "/data/backups";

    /// <summary>Directory for CSV metadata exports.</summary>
    public string ExportPath { get; set; } = "/data/exports";

    /// <summary>Directory for the local audit log file.</summary>
    public string LogPath { get; set; } = "/data/logs";

    /// <summary>Directory used to stage read-only copies of locked databases.</summary>
    public string WorkPath { get; set; } = "/tmp/edgepassmanager";

    /// <summary>Directory holding category blocklist files (downloaded + uploaded). Auto-loaded on start.</summary>
    public string ListDirectory { get; set; } = "/data/lists";

    /// <summary>Category name used for list files that don't encode a category in their filename.</summary>
    public string DefaultCategory { get; set; } = "adult";

    /// <summary>Blocklist categories, each optionally auto-refreshed from URLs.</summary>
    public List<CategoryDefinition> Categories { get; set; } = new();

    /// <summary>Enable the daily background refresh of category URLs.</summary>
    public bool AutoRefreshEnabled { get; set; } = true;

    /// <summary>How often the background refresh downloads category URLs.</summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>Maximum size accepted for a downloaded or uploaded category list.</summary>
    public long MaxListBytes { get; set; } = 30 * 1024 * 1024;

    /// <summary>Maximum number of unique valid domains accepted in one category list.</summary>
    public int MaxListDomains { get; set; } = 2_000_000;

    /// <summary>Deletes above this count require typed confirmation in the UI.</summary>
    public int LargeDeleteThreshold { get; set; } = 25;

    /// <summary>When true the app refuses all write/delete/restore operations.</summary>
    public bool ReadOnlyMode { get; set; } = true;
}

public sealed class EdgeDataRoot
{
    /// <summary>Label shown in the UI, e.g. "Stable", "Beta", "Dev".</summary>
    public string Channel { get; set; } = "Stable";

    /// <summary>Absolute path to an Edge "User Data" directory.</summary>
    public string Path { get; set; } = string.Empty;
}

public sealed class CategoryDefinition
{
    /// <summary>Lowercase category key, e.g. "adult", "gambling", "ads", "social".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>URLs (hosts or domain lists) auto-downloaded daily for this category.</summary>
    public List<string> Urls { get; set; } = new();

    /// <summary>Extra local files to load for this category.</summary>
    public List<string> Files { get; set; } = new();
}
