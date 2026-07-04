namespace EdgePasswordBulkManager.Helpers;

/// <summary>
/// Parses domain blocklists in mixed formats and performs suffix-based domain matching.
/// Supported lines:
///   - hosts format:  "0.0.0.0 example.com" / "127.0.0.1 example.com"
///   - plain domain:  "example.com"
///   - comments:      lines starting with '#' or '!' (and inline "# ...")
/// A host matches a set if it equals a listed domain or is a subdomain of one.
/// </summary>
public static class DomainListParser
{
    public static string? ParseLine(string raw)
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] is '#' or '!')
        {
            return null;
        }

        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            line = line[..hash].Trim();
            if (line.Length == 0)
            {
                return null;
            }
        }

        var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var domain = tokens.Length >= 2 ? tokens[^1] : tokens[0];

        domain = domain.TrimEnd('.').ToLowerInvariant();
        if (domain.StartsWith("www.", StringComparison.Ordinal))
        {
            domain = domain[4..];
        }

        if (!domain.Contains('.') || domain.Contains('/') || domain is "localhost" or "local" or "localhost.localdomain")
        {
            return null;
        }

        return domain;
    }

    /// <summary>Reads a list file, adding parsed domains into <paramref name="into"/>. Returns count added.</summary>
    public static int LoadFileInto(string path, HashSet<string> into)
    {
        var added = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var d = ParseLine(raw);
            if (d is not null && into.Add(d))
            {
                added++;
            }
        }
        return added;
    }

    /// <summary>Reads a stream of list content into a new set.</summary>
    public static async Task<HashSet<string>> LoadStreamAsync(Stream stream)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            var d = ParseLine(line);
            if (d is not null)
            {
                set.Add(d);
            }
        }
        return set;
    }

    /// <summary>Returns the matched list domain for a host, or null. Checks host then each parent domain.</summary>
    public static string? Match(string? host, IReadOnlySet<string> domains)
    {
        if (string.IsNullOrWhiteSpace(host) || domains.Count == 0)
        {
            return null;
        }

        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal))
        {
            h = h[4..];
        }

        var parts = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        for (var i = 0; i <= parts.Length - 2; i++)
        {
            var candidate = string.Join('.', parts.Skip(i));
            if (domains.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
