namespace EdgePasswordBulkManager.Helpers;

/// <summary>Helpers for deriving a normalized domain used for duplicate grouping.</summary>
public static class DomainHelper
{
    /// <summary>
    /// Extracts a lowercase host without a leading "www." from an origin URL or sign-on realm.
    /// Falls back to the raw input when it cannot be parsed as a URI.
    /// </summary>
    public static string Normalize(string? originUrl, string? signonRealm)
    {
        var host = ExtractHost(originUrl) ?? ExtractHost(signonRealm);
        if (string.IsNullOrWhiteSpace(host))
        {
            // signon_realm may look like "Host: example.com" for HTTP auth; take the last token.
            var raw = (signonRealm ?? originUrl ?? string.Empty).Trim().ToLowerInvariant();
            return raw;
        }

        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static string? ExtractHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            return uri.Host;
        }

        return null;
    }
}
