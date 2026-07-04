namespace EdgePasswordBulkManager.Helpers;

/// <summary>
/// Chromium stores timestamps as microseconds elapsed since 1601-01-01 00:00:00 UTC
/// (the Windows/WebKit epoch). This converts those integers to <see cref="DateTimeOffset"/>.
/// </summary>
public static class ChromiumTime
{
    private static readonly DateTimeOffset Epoch = new(1601, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset? FromMicroseconds(long microseconds)
    {
        if (microseconds <= 0)
        {
            return null;
        }

        try
        {
            return Epoch.AddMicroseconds(microseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Guard against corrupt/out-of-range values rather than crashing a load.
            return null;
        }
    }
}
