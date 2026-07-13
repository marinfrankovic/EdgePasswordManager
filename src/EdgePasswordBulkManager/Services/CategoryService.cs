using EdgePasswordBulkManager.Helpers;
using EdgePasswordBulkManager.Models;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Services;

/// <summary>
/// Loads one or more named domain-category blocklists (adult, gambling, …) from disk and
/// classifies hosts against them. Works fully offline. Files are discovered in the list
/// directory using the naming convention "&lt;category&gt;__&lt;anything&gt;.txt"; files without a
/// "__" prefix fall into the default category.
/// </summary>
public sealed class CategoryService
{
    private readonly AppOptions _options;
    private readonly AuditLog _audit;
    private readonly ILogger<CategoryService> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private Dictionary<string, HashSet<string>> _categories = new(StringComparer.OrdinalIgnoreCase);

    public CategoryService(IOptions<AppOptions> options, AuditLog audit, ILogger<CategoryService> logger)
    {
        _options = options.Value;
        _audit = audit;
        _logger = logger;
        Reload();
    }

    public string ListDirectory => _options.ListDirectory;

    public IReadOnlyList<string> Categories
    {
        get { lock (_gate) { return _categories.Keys.OrderBy(k => k).ToList(); } }
    }

    public int TotalDomains
    {
        get { lock (_gate) { return _categories.Values.Sum(s => s.Count); } }
    }

    public int CountFor(string category)
    {
        lock (_gate)
        {
            return _categories.TryGetValue(category, out var set) ? set.Count : 0;
        }
    }

    /// <summary>(Re)loads bundled + list-directory + configured category files.</summary>
    public void Reload()
    {
        var cats = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> Bucket(string name)
        {
            if (!cats.TryGetValue(name, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                cats[name] = set;
            }
            return set;
        }

        // 1) Bundled starter list ships with the app under the default category.
        var bundled = Path.Combine(AppContext.BaseDirectory, "adult-lists", "adult-domains.txt");
        SafeLoad(bundled, Bucket(_options.DefaultCategory));

        // 2) Everything in the list directory, category encoded in the filename before "__".
        try
        {
            if (Directory.Exists(_options.ListDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_options.ListDirectory, "*.txt"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var sep = name.IndexOf("__", StringComparison.Ordinal);
                    var category = sep > 0 ? name[..sep].ToLowerInvariant() : _options.DefaultCategory;
                    SafeLoad(file, Bucket(category));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed enumerating list directory {Dir}", _options.ListDirectory);
        }

        // 3) Explicit files declared per category in configuration.
        foreach (var def in _options.Categories)
        {
            foreach (var file in def.Files)
            {
                SafeLoad(Environment.ExpandEnvironmentVariables(file), Bucket(def.Name.ToLowerInvariant()));
            }
        }

        lock (_gate)
        {
            _categories = cats;
        }

        _audit.Write("category-load",
            string.Join(", ", cats.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value.Count}")));
    }

    /// <summary>Returns the category names whose lists match the host.</summary>
    public IReadOnlyList<string> Match(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Array.Empty<string>();
        }

        List<string>? matched = null;
        lock (_gate)
        {
            foreach (var (name, set) in _categories)
            {
                if (DomainListParser.Match(host, set) is not null)
                {
                    (matched ??= new List<string>()).Add(name);
                }
            }
        }

        return (IReadOnlyList<string>?)matched ?? Array.Empty<string>();
    }

    public void Classify(LoginEntry entry) => entry.Categories = Match(entry.NormalizedDomain).ToList();

    /// <summary>Saves an uploaded list into the list directory under a category, then reloads.</summary>
    public async Task<(int domains, int total)> ImportAsync(string category, string fileName, Stream content)
    {
        await _importGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_options.ListDirectory);
            var safeCat = Sanitize(string.IsNullOrWhiteSpace(category) ? _options.DefaultCategory : category);
            var safeName = Sanitize(Path.GetFileNameWithoutExtension(fileName));
            var dest = Path.Combine(_options.ListDirectory, $"{safeCat}__upload-{safeName}.txt");
            var tmp = dest + $".tmp-{Guid.NewGuid():N}";

            try
            {
                await using (var output = File.Create(tmp))
                {
                    await CopyWithLimitAsync(content, output, _options.MaxListBytes);
                    await output.FlushAsync();
                }

                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var added = DomainListParser.LoadFileInto(tmp, set);
                if (added == 0 || added > _options.MaxListDomains)
                {
                    throw new InvalidDataException(
                        $"Uploaded list contains {added:N0} valid domains; expected 1-{_options.MaxListDomains:N0}.");
                }

                File.Move(tmp, dest, overwrite: true);
                Reload();
                _audit.Write("category-import", $"{safeCat} <- {fileName} ({added} domains)");
                return (added, TotalDomains);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    private void SafeLoad(string path, HashSet<string> into)
    {
        try
        {
            if (File.Exists(path))
            {
                DomainListParser.LoadFileInto(path, into);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed loading list {Path}", path);
        }
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' ? c : '-').ToArray();
        var s = new string(chars).Trim('-').ToLowerInvariant();
        return string.IsNullOrEmpty(s) ? "list" : s;
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, long maxBytes)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException($"Uploaded list exceeds the {maxBytes:N0}-byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read));
        }
    }
}
