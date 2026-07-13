using System.Globalization;
using System.Text;
using EdgePasswordBulkManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Creates timestamped backup copies of the Login Data DB and exports selected-row
/// metadata to CSV. Passwords are never exported (the app never decrypts them).
/// </summary>
public sealed class BackupExportService
{
    private readonly AppOptions _options;
    private readonly AuditLog _audit;

    public BackupExportService(IOptions<AppOptions> options, AuditLog audit)
    {
        _options = options.Value;
        _audit = audit;
    }

    /// <summary>Creates a consistent SQLite snapshot in a unique timestamped backup folder.</summary>
    public string BackupLoginData(EdgeProfile profile)
    {
        Directory.CreateDirectory(_options.BackupPath);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var safeName = SafeStoreName(profile);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(_options.BackupPath, $"{safeName}_{stamp}-{suffix}");
        Directory.CreateDirectory(dir);

        try
        {
            CreateSnapshot(profile.LoginDataPath, Path.Combine(dir, "Login Data"));
        }
        catch
        {
            Directory.Delete(dir, recursive: true);
            throw;
        }

        _audit.Write("backup", dir);
        return dir;
    }

    /// <summary>Writes metadata-only CSV for the supplied rows. Never includes passwords.</summary>
    public string ExportCsv(EdgeProfile profile, IEnumerable<LoginEntry> rows)
    {
        Directory.CreateDirectory(_options.ExportPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeName = SafeStoreName(profile);
        var file = Path.Combine(_options.ExportPath, $"{safeName}_{stamp}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("origin_url,signon_realm,username,normalized_domain,date_created,date_last_used,times_used,blacklisted");
        foreach (var r in rows)
        {
            sb.Append(Csv(r.OriginUrl)).Append(',')
              .Append(Csv(r.SignonRealm)).Append(',')
              .Append(Csv(r.Username)).Append(',')
              .Append(Csv(r.NormalizedDomain)).Append(',')
              .Append(Csv(r.DateCreated?.ToString("u", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
              .Append(Csv(r.DateLastUsed?.ToString("u", CultureInfo.InvariantCulture) ?? string.Empty)).Append(',')
              .Append(r.TimesUsed.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(r.Blacklisted ? "true" : "false")
              .AppendLine();
        }

        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _audit.Write("export-csv", $"{file} ({rows.Count()} rows)");
        return file;
    }

    private static string Csv(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuote ? $"\"{escaped}\"" : escaped;
    }

    /// <summary>Builds a filesystem-safe backup/export prefix that is unique per store.</summary>
    public static string SafeStoreName(EdgeProfile profile)
    {
        var raw = $"{profile.Channel}_{profile.FolderName}_{profile.StoreFile}";
        var chars = raw.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return new string(chars);
    }

    public static void CreateSnapshot(string sourcePath, string destinationPath)
    {
        var sourceBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

        using var source = new SqliteConnection(sourceBuilder.ConnectionString);
        using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        ValidateDatabase(destination);
    }

    public static void ValidateDatabase(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        ValidateDatabase(connection);
    }

    private static void ValidateDatabase(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite integrity check failed: {result ?? "no result"}");
        }
    }
}
