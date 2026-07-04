using System.Text.RegularExpressions;
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
    public bool IsRefreshing { get; private set; }

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
        if (IsRefreshing)
        {
            return "already refreshing";
        }

        IsRefreshing = true;
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
                        var tmp = target + ".tmp";
                        await using (var resp = await client.GetStreamAsync(url, ct))
                        await using (var fs = File.Create(tmp))
                        {
                            await resp.CopyToAsync(fs, ct);
                        }
                        File.Move(tmp, target, overwrite: true);
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
            IsRefreshing = false;
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
}
