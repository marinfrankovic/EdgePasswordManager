using System.Text;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Minimal thread-safe, append-only audit log written to a local file.
/// Records timestamped actions only — never plaintext passwords (the app never has them).
/// </summary>
public sealed class AuditLog
{
    private readonly string _logDir;
    private readonly object _gate = new();
    private readonly ILogger<AuditLog> _logger;
    private readonly LinkedList<string> _recent = new();
    private const int MaxRecent = 200;

    public AuditLog(IConfiguration config, ILogger<AuditLog> logger)
    {
        _logger = logger;
        _logDir = config.GetValue<string>("EdgePassManager:LogPath") ?? "/data/logs";
        TryEnsureDir();
    }

    public IReadOnlyCollection<string> Recent
    {
        get { lock (_gate) { return _recent.ToArray(); } }
    }

    public void Write(string action, string? detail = null)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\t{action}" +
                   (string.IsNullOrEmpty(detail) ? string.Empty : $"\t{detail}");

        lock (_gate)
        {
            _recent.AddFirst(line);
            while (_recent.Count > MaxRecent)
            {
                _recent.RemoveLast();
            }

            try
            {
                var file = Path.Combine(_logDir, $"edgepassmanager-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write audit log line");
            }
        }

        _logger.LogInformation("AUDIT {Action} {Detail}", action, detail);
    }

    private void TryEnsureDir()
    {
        try
        {
            Directory.CreateDirectory(_logDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create log directory {Dir}", _logDir);
        }
    }
}
