using System.Text.Json;
using EdgePasswordBulkManager.Models;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Discovers Edge profiles by scanning configured "User Data" roots for folders
/// that contain a "Login Data" SQLite file, resolving friendly names from Local State.
/// </summary>
public sealed class ProfileDiscoveryService
{
    private readonly AppOptions _options;
    private readonly AuditLog _audit;
    private readonly ILogger<ProfileDiscoveryService> _logger;

    public ProfileDiscoveryService(IOptions<AppOptions> options, AuditLog audit, ILogger<ProfileDiscoveryService> logger)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
    }

    public IReadOnlyList<EdgeProfile> Discover()
    {
        var profiles = new List<EdgeProfile>();

        foreach (var root in _options.EdgeDataRoots)
        {
            var rootPath = string.IsNullOrWhiteSpace(root.Path)
                ? string.Empty
                : Environment.ExpandEnvironmentVariables(root.Path);

            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                _logger.LogWarning("Edge data root not found: {Path}", rootPath);
                continue;
            }

            var names = ReadProfileNames(rootPath);

            foreach (var dir in EnumerateProfileDirectories(rootPath))
            {
                var loginData = Path.Combine(dir, "Login Data");
                if (!File.Exists(loginData))
                {
                    continue;
                }

                var folder = Path.GetFileName(dir);
                var display = names.TryGetValue(folder, out var friendly) && !string.IsNullOrWhiteSpace(friendly)
                    ? friendly
                    : folder;

                profiles.Add(new EdgeProfile
                {
                    Channel = root.Channel,
                    FolderName = folder,
                    DisplayName = display,
                    LoginDataPath = loginData,
                });
            }
        }

        _audit.Write("discover", $"found {profiles.Count} profile(s)");
        return profiles
            .OrderBy(p => p.Channel)
            .ThenBy(p => p.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateProfileDirectories(string root)
    {
        // Chromium places each profile in its own directory directly under User Data.
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            // Skip obvious non-profile helper directories.
            if (name.Equals("System Profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return d;
        }
    }

    private Dictionary<string, string> ReadProfileNames(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localState = Path.Combine(root, "Local State");
        if (!File.Exists(localState))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (doc.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("info_cache", out var cache) &&
                cache.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in cache.EnumerateObject())
                {
                    if (item.Value.TryGetProperty("name", out var nameProp) &&
                        nameProp.ValueKind == JsonValueKind.String)
                    {
                        result[item.Name] = nameProp.GetString() ?? item.Name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Local State at {Path}", localState);
        }

        return result;
    }
}
