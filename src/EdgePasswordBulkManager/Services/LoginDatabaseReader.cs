using EdgePasswordBulkManager.Helpers;
using EdgePasswordBulkManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Reads saved-login metadata from a Chromium "Login Data" SQLite database.
/// The password_value BLOB is intentionally never selected or decrypted.
/// Reads are performed against a temporary copy so a live/locked Edge profile can still be listed.
/// </summary>
public sealed class LoginDatabaseReader
{
    private readonly AppOptions _options;
    private readonly AuditLog _audit;
    private readonly ILogger<LoginDatabaseReader> _logger;

    public LoginDatabaseReader(IOptions<AppOptions> options, AuditLog audit, ILogger<LoginDatabaseReader> logger)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Inspects the "logins" table shape so SQL is generated only from columns that exist.</summary>
    public static LoginSchema InspectSchema(SqliteConnection conn)
    {
        var cols = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info('logins');";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // column 1 of table_info is the column name.
                cols.Add(reader.GetString(1));
            }
        }

        bool Has(string name) => cols.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase));

        return new LoginSchema
        {
            Columns = cols,
            HasIdColumn = Has("id"),
            HasDateLastUsed = Has("date_last_used"),
            HasTimesUsed = Has("times_used"),
            HasBlacklisted = Has("blacklisted_by_user"),
        };
    }

    public LoadResult Load(string loginDataPath)
    {
        string? warning = null;
        var copyPath = CreateWorkingCopy(loginDataPath, out var usedCopy, out var copyWarning);
        warning = copyWarning;

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = copyPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };

            using var conn = new SqliteConnection(csb.ConnectionString);
            conn.Open();

            var schema = InspectSchema(conn);
            var entries = ReadEntries(conn, schema);
            MarkDuplicates(entries);

            _audit.Write("load", $"{Path.GetFileName(Path.GetDirectoryName(loginDataPath))}: {entries.Count} entries" +
                                 (usedCopy ? " (from copy)" : string.Empty));

            return new LoadResult
            {
                Entries = entries,
                Schema = schema,
                LoadedFromCopy = usedCopy,
                Warning = warning,
            };
        }
        finally
        {
            if (usedCopy)
            {
                TryDelete(copyPath);
            }
        }
    }

    private List<LoginEntry> ReadEntries(SqliteConnection conn, LoginSchema schema)
    {
        var select = new List<string>
        {
            schema.HasIdColumn ? "id" : "NULL AS id",
            "origin_url",
            "action_url",
            "signon_realm",
            "username_element",
            "username_value",
            "password_element",
            "date_created",
            schema.HasDateLastUsed ? "date_last_used" : "0 AS date_last_used",
            schema.HasTimesUsed ? "times_used" : "0 AS times_used",
            schema.HasBlacklisted ? "blacklisted_by_user" : "0 AS blacklisted_by_user",
        };

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", select)} FROM logins;";

        var list = new List<LoginEntry>();
        using var reader = cmd.ExecuteReader();

        int idxId = reader.GetOrdinal("id");
        int idxOrigin = reader.GetOrdinal("origin_url");
        int idxAction = reader.GetOrdinal("action_url");
        int idxRealm = reader.GetOrdinal("signon_realm");
        int idxUserEl = reader.GetOrdinal("username_element");
        int idxUser = reader.GetOrdinal("username_value");
        int idxPassEl = reader.GetOrdinal("password_element");
        int idxCreated = reader.GetOrdinal("date_created");
        int idxLast = reader.GetOrdinal("date_last_used");
        int idxTimes = reader.GetOrdinal("times_used");
        int idxBlack = reader.GetOrdinal("blacklisted_by_user");

        while (reader.Read())
        {
            var origin = reader.IsDBNull(idxOrigin) ? string.Empty : reader.GetString(idxOrigin);
            var realm = reader.IsDBNull(idxRealm) ? string.Empty : reader.GetString(idxRealm);

            list.Add(new LoginEntry
            {
                Id = reader.IsDBNull(idxId) ? null : reader.GetInt64(idxId),
                OriginUrl = origin,
                ActionUrl = reader.IsDBNull(idxAction) ? string.Empty : reader.GetString(idxAction),
                SignonRealm = realm,
                UsernameElement = reader.IsDBNull(idxUserEl) ? string.Empty : reader.GetString(idxUserEl),
                Username = reader.IsDBNull(idxUser) ? string.Empty : reader.GetString(idxUser),
                PasswordElement = reader.IsDBNull(idxPassEl) ? string.Empty : reader.GetString(idxPassEl),
                DateCreated = ChromiumTime.FromMicroseconds(reader.IsDBNull(idxCreated) ? 0 : reader.GetInt64(idxCreated)),
                DateLastUsed = ChromiumTime.FromMicroseconds(reader.IsDBNull(idxLast) ? 0 : reader.GetInt64(idxLast)),
                TimesUsed = reader.IsDBNull(idxTimes) ? 0 : (int)reader.GetInt64(idxTimes),
                Blacklisted = !reader.IsDBNull(idxBlack) && reader.GetInt64(idxBlack) != 0,
                NormalizedDomain = DomainHelper.Normalize(origin, realm),
            });
        }

        return list;
    }

    private static void MarkDuplicates(List<LoginEntry> entries)
    {
        var groups = entries
            .GroupBy(e => $"{e.NormalizedDomain}\u0001{e.Username}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            foreach (var e in group)
            {
                e.IsDuplicate = true;
            }
        }
    }

    private string CreateWorkingCopy(string source, out bool usedCopy, out string? warning)
    {
        warning = null;
        Directory.CreateDirectory(_options.WorkPath);
        var dest = Path.Combine(_options.WorkPath, $"logindata-{Guid.NewGuid():N}.sqlite");

        try
        {
            // Copy the main DB plus WAL/SHM sidecars so a WAL-mode DB reads a consistent snapshot.
            File.Copy(source, dest, overwrite: true);
            CopySidecar(source + "-wal", dest + "-wal");
            CopySidecar(source + "-shm", dest + "-shm");
            usedCopy = true;
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy Login Data; reading original in read-only mode");
            warning = "Could not create a working copy; read directly in read-only mode.";
            usedCopy = false;
            return source;
        }
    }

    private static void CopySidecar(string src, string dst)
    {
        if (File.Exists(src))
        {
            File.Copy(src, dst, overwrite: true);
        }
    }

    private void TryDelete(string path)
    {
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete working copy {Path}", p);
            }
        }
    }
}
