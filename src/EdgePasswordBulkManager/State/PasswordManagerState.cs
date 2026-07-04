using EdgePasswordBulkManager.Helpers;
using EdgePasswordBulkManager.Models;
using EdgePasswordBulkManager.Services;

namespace EdgePasswordBulkManager.State;

public enum SortColumn { Origin, Realm, Username, Created, LastUsed, TimesUsed, Profile }

/// <summary>
/// Scoped (per-circuit) view-model: holds the loaded profile(s), entries, filters,
/// selection and derived views. Supports single-profile and cross-profile aggregate mode.
/// </summary>
public sealed class PasswordManagerState
{
    private readonly ProfileDiscoveryService _discovery;
    private readonly LoginDatabaseReader _reader;
    private readonly DeleteService _delete;
    private readonly CategoryService _categories;

    public PasswordManagerState(
        ProfileDiscoveryService discovery,
        LoginDatabaseReader reader,
        DeleteService delete,
        CategoryService categories)
    {
        _discovery = discovery;
        _reader = reader;
        _delete = delete;
        _categories = categories;
    }

    public event Action? Changed;

    public IReadOnlyList<EdgeProfile> Profiles { get; private set; } = Array.Empty<EdgeProfile>();
    public EdgeProfile? SelectedProfile { get; private set; }
    public bool Aggregate { get; private set; }
    public LoginSchema? Schema { get; private set; }
    public bool LoadedFromCopy { get; private set; }
    public string? LoadWarning { get; private set; }
    public bool IsLoading { get; private set; }

    public CategoryService CategoryService => _categories;

    private List<LoginEntry> _all = new();

    // Filters
    public string SearchText { get; set; } = string.Empty;
    public string SiteFilter { get; set; } = string.Empty;
    public string UsernameFilter { get; set; } = string.Empty;
    public bool DuplicatesOnly { get; set; }
    public string CategoryFilter { get; set; } = string.Empty; // "" = all, "__any__" = any flagged, else category

    // Sort
    public SortColumn Sort { get; private set; } = SortColumn.Origin;
    public bool SortDescending { get; private set; }

    public int TotalCount => _all.Count;
    public int FilteredCount => Filtered().Count();
    public int SelectedCount => _all.Count(e => e.Selected);
    public int AdultCount => _all.Count(e => e.IsAdult);
    public int DuplicateCount => _all.Count(e => e.IsDuplicate);
    public int InsecureCount => _all.Count(e => e.IsInsecure);
    public int NeverUsedCount => _all.Count(e => e.NeverUsed);

    public void NotifyChanged() => Changed?.Invoke();

    public void Refresh()
    {
        Profiles = _discovery.Discover();
        if (!Aggregate && SelectedProfile is not null)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Key == SelectedProfile.Key);
        }
        NotifyChanged();
    }

    public async Task SelectProfileAsync(string key)
    {
        Aggregate = false;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Key == key);
        await LoadAsync();
    }

    public async Task SetAggregateAsync(bool on)
    {
        Aggregate = on;
        if (on)
        {
            SelectedProfile = null;
            await LoadAsync();
        }
        else
        {
            _all = new List<LoginEntry>();
            NotifyChanged();
        }
    }

    public async Task LoadAsync()
    {
        if (!Aggregate && SelectedProfile is null)
        {
            return;
        }

        IsLoading = true;
        NotifyChanged();
        try
        {
            var targets = Aggregate ? Profiles.ToList()
                : SelectedProfile is null ? new List<EdgeProfile>()
                : new List<EdgeProfile> { SelectedProfile };

            var (entries, schema, fromCopy, warning) = await Task.Run(() =>
            {
                var all = new List<LoginEntry>();
                LoginSchema? sch = null;
                var copy = false;
                string? warn = null;

                foreach (var profile in targets)
                {
                    var result = _reader.Load(profile.LoginDataPath);
                    sch = result.Schema;
                    copy |= result.LoadedFromCopy;
                    warn ??= result.Warning;
                    foreach (var e in result.Entries)
                    {
                        e.ProfileKey = profile.Key;
                        e.ProfileLabel = profile.Label;
                        _categories.Classify(e);
                        all.Add(e);
                    }
                }
                return (all, sch, copy, warn);
            });

            _all = entries;
            Schema = schema;
            LoadedFromCopy = fromCopy;
            LoadWarning = warning;
            if (SelectedProfile is not null)
            {
                SelectedProfile.EntryCount = _all.Count;
            }
        }
        finally
        {
            IsLoading = false;
            NotifyChanged();
        }
    }

    public void ReclassifyAll()
    {
        foreach (var e in _all)
        {
            _categories.Classify(e);
        }
        NotifyChanged();
    }

    public void SetSort(SortColumn column)
    {
        if (Sort == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            Sort = column;
            SortDescending = false;
        }
        NotifyChanged();
    }

    public IEnumerable<LoginEntry> Filtered()
    {
        IEnumerable<LoginEntry> q = _all;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var s = SearchText.Trim();
            q = q.Where(e =>
                e.OriginUrl.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.SignonRealm.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.Username.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SiteFilter))
        {
            var s = SiteFilter.Trim();
            q = q.Where(e =>
                e.OriginUrl.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                e.SignonRealm.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(UsernameFilter))
        {
            var s = UsernameFilter.Trim();
            q = q.Where(e => e.Username.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        if (DuplicatesOnly)
        {
            q = q.Where(e => e.IsDuplicate);
        }

        if (CategoryFilter == "__any__")
        {
            q = q.Where(e => e.Categories.Count > 0);
        }
        else if (!string.IsNullOrEmpty(CategoryFilter))
        {
            q = q.Where(e => e.Categories.Contains(CategoryFilter));
        }

        return q;
    }

    public IReadOnlyList<LoginEntry> Visible()
    {
        var q = Filtered();
        Func<LoginEntry, object?> key = Sort switch
        {
            SortColumn.Origin => e => e.OriginUrl,
            SortColumn.Realm => e => e.SignonRealm,
            SortColumn.Username => e => e.Username,
            SortColumn.Created => e => e.DateCreated ?? DateTimeOffset.MinValue,
            SortColumn.LastUsed => e => e.DateLastUsed ?? DateTimeOffset.MinValue,
            SortColumn.TimesUsed => e => e.TimesUsed,
            SortColumn.Profile => e => e.ProfileLabel,
            _ => e => e.OriginUrl,
        };

        q = SortDescending ? q.OrderByDescending(key) : q.OrderBy(key);
        return q.ToList();
    }

    // ---- Selection operations ----

    public void SelectVisible()
    {
        foreach (var e in Filtered()) e.Selected = true;
        NotifyChanged();
    }

    public void ClearSelection()
    {
        foreach (var e in _all) e.Selected = false;
        NotifyChanged();
    }

    public void InvertSelection()
    {
        foreach (var e in Filtered().ToList()) e.Selected = !e.Selected;
        NotifyChanged();
    }

    public void SelectByCategory(string category)
    {
        foreach (var e in Filtered().Where(e =>
                     category == "__any__" ? e.Categories.Count > 0 : e.Categories.Contains(category)))
        {
            e.Selected = true;
        }
        NotifyChanged();
    }

    public void SelectNeverUsed()
    {
        foreach (var e in Filtered().Where(e => e.NeverUsed)) e.Selected = true;
        NotifyChanged();
    }

    public void SelectInsecure()
    {
        foreach (var e in Filtered().Where(e => e.IsInsecure)) e.Selected = true;
        NotifyChanged();
    }

    /// <summary>Selects every duplicate except the newest per (domain, username) group — cleanup helper.</summary>
    public int SelectDuplicateExtras()
    {
        var groups = _all
            .Where(e => e.IsDuplicate)
            .GroupBy(e => $"{e.NormalizedDomain}\u0001{e.Username}\u0001{e.ProfileKey}", StringComparer.OrdinalIgnoreCase);

        var count = 0;
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(e => e.DateLastUsed ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.DateCreated ?? DateTimeOffset.MinValue)
                .ToList();

            foreach (var e in ordered.Skip(1))
            {
                e.Selected = true;
                count++;
            }
        }
        NotifyChanged();
        return count;
    }

    /// <summary>Selects entries whose domain matches an uploaded domain list. Returns match count.</summary>
    public async Task<int> SelectByDomainsAsync(Stream content)
    {
        var set = await DomainListParser.LoadStreamAsync(content);
        var count = 0;
        foreach (var e in _all.Where(e => DomainListParser.Match(e.NormalizedDomain, set) is not null))
        {
            e.Selected = true;
            count++;
        }
        NotifyChanged();
        return count;
    }

    public IReadOnlyList<LoginEntry> SelectedEntries() => _all.Where(e => e.Selected).ToList();

    /// <summary>Deletes (or dry-runs) the selection, grouping rows by their source profile.</summary>
    public async Task<DeleteResult> DeleteSelectedAsync(bool dryRun)
    {
        var rows = SelectedEntries();
        if (rows.Count == 0)
        {
            return new DeleteResult { DryRun = dryRun, FatalError = "No rows selected." };
        }

        var merged = new DeleteResult { DryRun = dryRun, Committed = !dryRun };

        foreach (var group in rows.GroupBy(r => r.ProfileKey))
        {
            var profile = Profiles.FirstOrDefault(p => p.Key == group.Key);
            if (profile is null)
            {
                merged.FatalError = (merged.FatalError ?? "") + $"Profile {group.Key} not found. ";
                merged.Committed = false;
                continue;
            }

            var groupRows = group.ToList();
            var res = await Task.Run(() => _delete.Execute(profile, groupRows, dryRun));

            merged.Rows.AddRange(res.Rows);
            merged.BackupPaths.AddRange(res.BackupPaths);
            if (!string.IsNullOrEmpty(res.FatalError))
            {
                merged.FatalError = (merged.FatalError ?? "") + res.FatalError + " ";
            }
            if (!dryRun && !res.Committed)
            {
                merged.Committed = false;
            }
        }

        if (!dryRun && merged.SuccessCount > 0)
        {
            RemoveByKeys(merged.Rows.Where(r => r.Success).Select(r => r.RowKey));
        }

        return merged;
    }

    public void RemoveByKeys(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet();
        _all.RemoveAll(e => set.Contains(e.RowKey));
        if (SelectedProfile is not null)
        {
            SelectedProfile.EntryCount = _all.Count;
        }
        NotifyChanged();
    }

    public async Task<(int domains, int total)> ImportCategoryAsync(string category, string fileName, Stream content)
    {
        var res = await _categories.ImportAsync(category, fileName, content);
        ReclassifyAll();
        return res;
    }
}
