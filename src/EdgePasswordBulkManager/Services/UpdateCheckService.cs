using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgePasswordBulkManager.Services;

public sealed class UpdateCheckService(HttpClient httpClient)
{
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/marinfrankovic/EdgePasswordManager/releases/latest";

    public string CurrentVersion { get; } = GetCurrentVersion();

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync(LatestReleaseUrl, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.NoRelease(CurrentVersion);
            }

            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken);
            if (release is null || !TryParseVersion(release.TagName, out var latestVersion))
            {
                return UpdateCheckResult.Failed(CurrentVersion, "The latest release has an invalid version tag.");
            }

            TryParseVersion(CurrentVersion, out var currentVersion);
            return new UpdateCheckResult(
                CurrentVersion,
                latestVersion.ToString(3),
                latestVersion > currentVersion,
                release.Name,
                release.Body,
                release.HtmlUrl,
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(CurrentVersion, "The update check timed out.");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed(CurrentVersion, $"Update check failed: {ex.Message}");
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(CurrentVersion, "GitHub returned an invalid update response.");
        }
    }

    internal static bool TryParseVersion(string? value, out Version version)
    {
        var normalized = value?.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (Version.TryParse(normalized, out var parsed))
        {
            version = new Version(
                parsed.Major,
                Math.Max(parsed.Minor, 0),
                Math.Max(parsed.Build, 0));
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    private static string GetCurrentVersion()
    {
        var informational = typeof(UpdateCheckService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParseVersion(informational, out var version) ? version.ToString(3) : "0.0.0";
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl);
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseName,
    string? ReleaseNotes,
    string? ReleaseUrl,
    string? Error)
{
    public static UpdateCheckResult NoRelease(string currentVersion) =>
        new(currentVersion, null, false, null, null, null, "No published release is available yet.");

    public static UpdateCheckResult Failed(string currentVersion, string error) =>
        new(currentVersion, null, false, null, null, null, error);
}