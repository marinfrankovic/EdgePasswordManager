using EdgePasswordBulkManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Deletes selected rows from the "logins" table using a single SQLite transaction.
/// Rows are matched by the stable "id" column when available, otherwise by the legacy
/// composite key. A DB backup is taken before any destructive write.
/// </summary>
public sealed class DeleteService
{
    private readonly AppOptions _options;
    private readonly BackupExportService _backup;
    private readonly AuditLog _audit;
    private readonly ILogger<DeleteService> _logger;

    public DeleteService(
        IOptions<AppOptions> options,
        BackupExportService backup,
        AuditLog audit,
        ILogger<DeleteService> logger)
    {
        _options = options.Value;
        _backup = backup;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Runs a delete or a dry-run. In dry-run mode nothing is written and no backup is taken;
    /// each candidate row is counted via a SELECT so the report reflects real match counts.
    /// </summary>
    public DeleteResult Execute(EdgeProfile profile, IReadOnlyList<LoginEntry> rows, bool dryRun)
    {
        if (_options.ReadOnlyMode && !dryRun)
        {
            return new DeleteResult
            {
                DryRun = dryRun,
                FatalError = "Application is running in read-only mode; deletes are disabled.",
            };
        }

        if (rows.Count == 0)
        {
            return new DeleteResult { DryRun = dryRun, FatalError = "No rows selected." };
        }

        if (dryRun)
        {
            return DryRun(profile, rows);
        }

        return CommitDelete(profile, rows);
    }

    private DeleteResult DryRun(EdgeProfile profile, IReadOnlyList<LoginEntry> rows)
    {
        var result = new DeleteResult { DryRun = true };

        try
        {
            using var conn = OpenReadOnlyCopyless(profile.LoginDataPath);
            var schema = LoginDatabaseReader.InspectSchema(conn);

            foreach (var row in rows)
            {
                using var cmd = conn.CreateCommand();
                var where = BuildWhere(cmd, row, schema);
                cmd.CommandText = $"SELECT COUNT(*) FROM logins WHERE {where};";
                var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
                result.Rows.Add(new DeleteRowResult
                {
                    RowKey = row.RowKey,
                    OriginUrl = row.OriginUrl,
                    Username = row.Username,
                    Success = count > 0,
                    RowsAffected = count,
                    Error = count == 0 ? "No matching row (already deleted?)" : null,
                });
            }
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            result.FatalError = LockedMessage;
        }
        catch (Exception ex)
        {
            result.FatalError = ex.Message;
        }

        _audit.Write("dry-run", $"{profile.Key}: {result.SuccessCount}/{rows.Count} would match");
        return result;
    }

    private DeleteResult CommitDelete(EdgeProfile profile, IReadOnlyList<LoginEntry> rows)
    {
        // Always back up before touching the live database.
        string? backupPath;
        try
        {
            backupPath = _backup.BackupLoginData(profile);
        }
        catch (Exception ex)
        {
            return new DeleteResult
            {
                DryRun = false,
                FatalError = $"Backup failed, aborting delete: {ex.Message}",
            };
        }

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = profile.LoginDataPath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };

            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();

            var schema = LoginDatabaseReader.InspectSchema(conn);
            using var tx = conn.BeginTransaction();

            var perRow = new List<DeleteRowResult>();
            try
            {
                foreach (var row in rows)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    var where = BuildWhere(cmd, row, schema);
                    cmd.CommandText = $"DELETE FROM logins WHERE {where};";
                    var affected = cmd.ExecuteNonQuery();

                    perRow.Add(new DeleteRowResult
                    {
                        RowKey = row.RowKey,
                        OriginUrl = row.OriginUrl,
                        Username = row.Username,
                        Success = affected > 0,
                        RowsAffected = affected,
                        Error = affected == 0 ? "No matching row (already deleted?)" : null,
                    });
                }

                tx.Commit();

                var committed = new DeleteResult
                {
                    DryRun = false,
                    Committed = true,
                    BackupPaths = { backupPath },
                };
                committed.Rows.AddRange(perRow);
                _audit.Write("delete", $"{profile.Key}: deleted {committed.TotalRowsAffected} row(s), backup {backupPath}");
                return committed;
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, "Delete transaction rolled back");
                _audit.Write("delete-rollback", $"{profile.Key}: {ex.Message}");
                return new DeleteResult
                {
                    DryRun = false,
                    Committed = false,
                    BackupPaths = { backupPath },
                    FatalError = $"Transaction rolled back, no changes applied: {ex.Message}",
                };
            }
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            _audit.Write("delete-locked", profile.Key);
            return new DeleteResult
            {
                DryRun = false,
                BackupPaths = { backupPath },
                FatalError = LockedMessage,
            };
        }
        catch (Exception ex)
        {
            _audit.Write("delete-error", $"{profile.Key}: {ex.Message}");
            return new DeleteResult
            {
                DryRun = false,
                BackupPaths = { backupPath },
                FatalError = ex.Message,
            };
        }
    }

    /// <summary>
    /// Builds a parameterized WHERE clause identifying exactly one row.
    /// Uses "id" when present, otherwise the Chromium composite primary key.
    /// </summary>
    private static string BuildWhere(SqliteCommand cmd, LoginEntry row, LoginSchema schema)
    {
        if (schema.HasIdColumn && row.Id is not null)
        {
            cmd.Parameters.AddWithValue("@id", row.Id.Value);
            return "id = @id";
        }

        cmd.Parameters.AddWithValue("@origin", row.OriginUrl);
        cmd.Parameters.AddWithValue("@uel", row.UsernameElement);
        cmd.Parameters.AddWithValue("@uval", row.Username);
        cmd.Parameters.AddWithValue("@pel", row.PasswordElement);
        cmd.Parameters.AddWithValue("@realm", row.SignonRealm);
        return "origin_url = @origin AND username_element = @uel AND username_value = @uval " +
               "AND password_element = @pel AND signon_realm = @realm";
    }

    private static SqliteConnection OpenReadOnlyCopyless(string path)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var conn = new SqliteConnection(csb.ConnectionString);
        conn.Open();
        return conn;
    }

    private static bool IsLocked(SqliteException ex)
        => ex.SqliteErrorCode is 5 /*BUSY*/ or 6 /*LOCKED*/;

    private const string LockedMessage =
        "The Login Data database is locked. Close Microsoft Edge completely (check Task Manager for lingering " +
        "msedge.exe processes) and try again. Deletes require exclusive write access.";
}
