using System.Globalization;
using EdgePasswordBulkManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Lists the timestamped backups this tool created and restores one over the live
/// Login Data database. A safety backup of the current state is taken before restoring.
/// </summary>
public sealed class RestoreService
{
    private readonly AppOptions _options;
    private readonly BackupExportService _backup;
    private readonly AuditLog _audit;
    private readonly ILogger<RestoreService> _logger;

    public RestoreService(
        IOptions<AppOptions> options,
        BackupExportService backup,
        AuditLog audit,
        ILogger<RestoreService> logger)
    {
        _options = options.Value;
        _backup = backup;
        _audit = audit;
        _logger = logger;
    }

    public IReadOnlyList<BackupInfo> ListBackups(EdgeProfile profile)
    {
        var result = new List<BackupInfo>();
        if (!Directory.Exists(_options.BackupPath))
        {
            return result;
        }

        var prefix = $"{profile.Channel}_{profile.FolderName}_".Replace(Path.DirectorySeparatorChar, '_');

        foreach (var dir in Directory.EnumerateDirectories(_options.BackupPath))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stampPart = name[prefix.Length..];
            DateTimeOffset when = Directory.GetLastWriteTime(dir);
            if (DateTime.TryParseExact(stampPart, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                when = new DateTimeOffset(parsed);
            }

            result.Add(new BackupInfo
            {
                Path = dir,
                When = when,
                ProfileKey = profile.Key,
                HasLoginData = File.Exists(Path.Combine(dir, "Login Data")),
            });
        }

        return result.OrderByDescending(b => b.When).ToList();
    }

    public RestoreResult Restore(EdgeProfile profile, string backupDir)
    {
        if (_options.ReadOnlyMode)
        {
            return new RestoreResult { Success = false, Error = "Read-only mode is enabled." };
        }

        var source = Path.Combine(backupDir, "Login Data");
        if (!File.Exists(source))
        {
            return new RestoreResult { Success = false, Error = "Backup does not contain a 'Login Data' file." };
        }

        // Snapshot the current state first so a restore is itself reversible.
        string? safety;
        try
        {
            safety = _backup.BackupLoginData(profile);
        }
        catch (Exception ex)
        {
            return new RestoreResult { Success = false, Error = $"Safety backup failed: {ex.Message}" };
        }

        try
        {
            var target = profile.LoginDataPath;
            File.Copy(source, target, overwrite: true);
            SyncSidecar(source + "-wal", target + "-wal");
            SyncSidecar(source + "-shm", target + "-shm");

            _audit.Write("restore", $"{profile.Key} <- {backupDir} (safety {safety})");
            return new RestoreResult { Success = true, SafetyBackupPath = safety };
        }
        catch (IOException ex)
        {
            _audit.Write("restore-locked", profile.Key);
            return new RestoreResult
            {
                Success = false,
                SafetyBackupPath = safety,
                Error = "Could not write Login Data — close Microsoft Edge completely and try again. " + ex.Message,
            };
        }
        catch (Exception ex)
        {
            return new RestoreResult { Success = false, SafetyBackupPath = safety, Error = ex.Message };
        }
    }

    private static void SyncSidecar(string src, string dst)
    {
        if (File.Exists(src))
        {
            File.Copy(src, dst, overwrite: true);
        }
        else if (File.Exists(dst))
        {
            // Remove a stale sidecar so it can't override the restored main DB.
            File.Delete(dst);
        }
    }
}
