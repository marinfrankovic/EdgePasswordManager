using System.Text.RegularExpressions;
using EdgePasswordBulkManager.Helpers;
using EdgePasswordBulkManager.Models;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Background service that downloads configured category list URLs into the list directory
/// on a daily schedule (and on demand), then reloads the <see cref="CategoryService"/>.
/// Downloads happen only when a target file is missing or older than the refresh interval.
/// </summary>
public sealed class ListRefreshService : BackgroundService
{
    private readonly AppOptions _options;
    private readonly CategoryService _categories;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AuditLog _audit;
    private readonly ILogger<ListRefreshService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public ListRefreshService(
        IOptions<AppOptions> options,
        CategoryService categories,
        IHttpClientFactory httpFactory,
        AuditLog audit,
        ILogger<ListRefreshService> logger)
    {
        _options = options.Value;
        _categories = categories;
        _httpFactory = httpFactory;
        _audit = audit;
        _logger = logger;
    }

    public DateTimeOffset? LastRefresh { get; private set; }
    public string LastStatus { get; private set; } = "not run yet";
    public bool IsRefreshing => _refreshGate.CurrentCount == 0;

    public static string FileNameForUrl(string category, string url)
    {
        var slug = url;
        try
        {
            var uri = new Uri(url);
            slug = uri.Host + uri.AbsolutePath;
        }
        catch { /* keep raw */ }

        slug = Regex.Replace(slug, "[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant();
        var cat = Regex.Replace(category, "[^a-zA-Z0-9]+", "-").Trim('-').ToLowerInvariant();
        return $"{cat}__{slug}.txt";
    }

    /// <summary>Downloads stale/missing URLs (or all when forced), then reloads categories.</summary>
    public async Task<string> RefreshNowAsync(bool force, CancellationToken ct)
    {
        if (!await _refreshGate.WaitAsync(0, ct))
        {
            return "already refreshing";
        }

        var downloaded = 0;
        var failed = 0;
        try
        {
            Directory.CreateDirectory(_options.ListDirectory);
            var interval = TimeSpan.FromHours(Math.Max(1, _options.RefreshIntervalHours));
            var client = _httpFactory.CreateClient("lists");

            foreach (var def in _options.Categories)
            {
                foreach (var url in def.Urls)
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var target = Path.Combine(_options.ListDirectory, FileNameForUrl(def.Name, url));
                    var stale = !File.Exists(target) ||
                                (DateTimeOffset.Now - File.GetLastWriteTime(target)) > interval;

                    if (!force && !stale)
                    {
                        continue;
                    }

                    try
                    {
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestedUri) ||
                            requestedUri.Scheme != Uri.UriSchemeHttps)
                        {
                            throw new InvalidOperationException("Category list URLs must use HTTPS.");
                        }

                        var tmp = target + $".tmp-{Guid.NewGuid():N}";
                        try
                        {
                            using var response = await client.GetAsync(
                                requestedUri, HttpCompletionOption.ResponseHeadersRead, ct);
                            response.EnsureSuccessStatusCode();

                            var finalUri = response.RequestMessage?.RequestUri;
                            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps)
                            {
                                throw new InvalidOperationException("Category list redirects must remain on HTTPS.");
                            }

                            var contentLength = response.Content.Headers.ContentLength;
                            if (contentLength > _options.MaxListBytes)
                            {
                                throw new InvalidDataException(
                                    $"Category list exceeds the {_options.MaxListBytes:N0}-byte limit.");
                            }

                            var mediaType = response.Content.Headers.ContentType?.MediaType;
                            if (mediaType is not null &&
                                !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
                                !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException($"Unsupported category list content type: {mediaType}");
                            }

                            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                            await using (var output = File.Create(tmp))
                            {
                                await CopyWithLimitAsync(responseStream, output, _options.MaxListBytes, ct);
                                await output.FlushAsync(ct);
                            }

                            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var validDomains = DomainListParser.LoadFileInto(tmp, domains);
                            if (validDomains == 0 || validDomains > _options.MaxListDomains)
                            {
                                throw new InvalidDataException(
                                    $"Category list contains {validDomains:N0} valid domains; expected 1-{_options.MaxListDomains:N0}.");
                            }

                            File.Move(tmp, target, overwrite: true);
                        }
                        finally
                        {
                            if (File.Exists(tmp))
                            {
                                File.Delete(tmp);
                            }
                        }
                        downloaded++;
                        _audit.Write("list-download", $"{def.Name} <- {url}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Failed downloading list {Url}", url);
                    }
                }
            }

            if (downloaded > 0)
            {
                _categories.Reload();
            }

            LastRefresh = DateTimeOffset.Now;
            LastStatus = $"{downloaded} downloaded, {failed} failed, {_categories.TotalDomains:N0} domains";
            return LastStatus;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoRefreshEnabled)
        {
            _logger.LogInformation("Auto-refresh disabled; lists load from disk only.");
            return;
        }

        // Give the app a moment to finish startup before any large download.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshNowAsync(force: false, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "List refresh cycle failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(Math.Max(1, _options.RefreshIntervalHours)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Category list exceeds the {maxBytes:N0}-byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
