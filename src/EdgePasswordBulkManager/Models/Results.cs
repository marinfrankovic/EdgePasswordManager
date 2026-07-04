namespace EdgePasswordBulkManager.Models;

/// <summary>Describes the detected shape of the "logins" table for safe SQL generation.</summary>
public sealed class LoginSchema
{
    public required IReadOnlyList<string> Columns { get; init; }
    public bool HasIdColumn { get; init; }
    public bool HasDateLastUsed { get; init; }
    public bool HasTimesUsed { get; init; }
    public bool HasBlacklisted { get; init; }
}

/// <summary>Result of loading a profile's logins, including schema info for the debug panel.</summary>
public sealed class LoadResult
{
    public required IReadOnlyList<LoginEntry> Entries { get; init; }
    public required LoginSchema Schema { get; init; }
    public bool LoadedFromCopy { get; init; }
    public string? Warning { get; init; }
}

/// <summary>Outcome of deleting one row.</summary>
public sealed class DeleteRowResult
{
    public required string RowKey { get; init; }
    public required string OriginUrl { get; init; }
    public required string Username { get; init; }
    public bool Success { get; init; }
    public int RowsAffected { get; init; }
    public string? Error { get; init; }
}

/// <summary>Aggregate result of a delete (or dry-run) operation, possibly spanning profiles.</summary>
public sealed class DeleteResult
{
    public bool DryRun { get; init; }
    public bool Committed { get; set; }
    public List<string> BackupPaths { get; init; } = new();
    public string? FatalError { get; set; }
    public List<DeleteRowResult> Rows { get; init; } = new();

    public int SuccessCount => Rows.Count(r => r.Success);
    public int FailureCount => Rows.Count(r => !r.Success);
    public int TotalRowsAffected => Rows.Sum(r => r.RowsAffected);
}

/// <summary>A backup folder created before a delete/restore.</summary>
public sealed class BackupInfo
{
    public required string Path { get; init; }
    public required DateTimeOffset When { get; init; }
    public required string ProfileKey { get; init; }
    public bool HasLoginData { get; init; }
}

/// <summary>Result of restoring a backup over the live Login Data DB.</summary>
public sealed class RestoreResult
{
    public bool Success { get; init; }
    public string? SafetyBackupPath { get; init; }
    public string? Error { get; init; }
}
