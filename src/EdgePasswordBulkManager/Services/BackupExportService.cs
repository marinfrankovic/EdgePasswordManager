using System.Globalization;
using System.Text;
using EdgePasswordBulkManager.Models;
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

    /// <summary>Copies the Login Data DB (and WAL/SHM sidecars) into a timestamped backup folder.</summary>
    public string BackupLoginData(EdgeProfile profile)
    {
        Directory.CreateDirectory(_options.BackupPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeName = SafeStoreName(profile);
        var dir = Path.Combine(_options.BackupPath, $"{safeName}_{stamp}");
        Directory.CreateDirectory(dir);

        var src = profile.LoginDataPath;
        File.Copy(src, Path.Combine(dir, "Login Data"), overwrite: true);
        CopyIfExists(src + "-wal", Path.Combine(dir, "Login Data-wal"));
        CopyIfExists(src + "-shm", Path.Combine(dir, "Login Data-shm"));

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

    private static void CopyIfExists(string src, string dst)
    {
        if (File.Exists(src))
        {
            File.Copy(src, dst, overwrite: true);
        }
    }
}
