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

        var prefix = BackupExportService.SafeStoreName(profile) + "_";

        foreach (var dir in Directory.EnumerateDirectories(_options.BackupPath))
        {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stampPart = name[prefix.Length..];
            DateTimeOffset when = Directory.GetLastWriteTime(dir);
                var timestamp = stampPart.Length >= 19 ? stampPart[..19] : stampPart;
                var format = timestamp.Length == 19 ? "yyyyMMdd-HHmmss-fff" : "yyyyMMdd-HHmmss";
                if (DateTime.TryParseExact(timestamp, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
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
            BackupExportService.ValidateDatabase(source);
            EnsureExclusiveAccess(target);
            ReplaceDatabase(source, target);

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

    private static void EnsureExclusiveAccess(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }
        using var rollback = connection.CreateCommand();
        rollback.CommandText = "ROLLBACK;";
        rollback.ExecuteNonQuery();
    }

    private static void ReplaceDatabase(string source, string target)
    {
        var token = Guid.NewGuid().ToString("N");
        var staged = target + $".restore-{token}";
        var rollback = target + $".rollback-{token}";
        var rollbackWal = rollback + "-wal";
        var rollbackShm = rollback + "-shm";
        var originalMoved = false;

        BackupExportService.CreateSnapshot(source, staged);

        try
        {
            MoveRequired(target, rollback, "stage the current database for rollback");
            originalMoved = true;
            MoveIfExists(target + "-wal", rollbackWal);
            MoveIfExists(target + "-shm", rollbackShm);
            MoveRequired(staged, target, "activate the restored database");
            BackupExportService.ValidateDatabase(target);
            DeleteIfExists(rollback);
            DeleteIfExists(rollbackWal);
            DeleteIfExists(rollbackShm);
        }
        catch
        {
            if (originalMoved)
            {
                DeleteIfExists(target);
                MoveIfExists(rollback, target);
                MoveIfExists(rollbackWal, target + "-wal");
                MoveIfExists(rollbackShm, target + "-shm");
            }
            throw;
        }
        finally
        {
            DeleteIfExists(staged);
        }
    }

    private static void MoveIfExists(string source, string target)
    {
        if (File.Exists(source))
        {
            File.Move(source, target, overwrite: true);
        }
    }

    private static void MoveRequired(string source, string target, string operation)
    {
        try
        {
            File.Move(source, target);
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not {operation}: {ex.Message}", ex);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
