namespace QuickPods.Core.Models;

public static class QuickPodsRepository
{
    public const string Slug = "Miquottty/QuickPods";

    public static Uri LatestReleaseApi { get; } =
        new($"https://api.github.com/repos/{Slug}/releases/latest");

    // Release pages outside this path are rejected, both when checking and when
    // restoring a cached result from settings.
    public static bool IsReleasePage(Uri? releasePage) =>
        releasePage is { IsAbsoluteUri: true } &&
        releasePage.Scheme == Uri.UriSchemeHttps &&
        string.Equals(releasePage.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        releasePage.AbsolutePath.StartsWith($"/{Slug}/releases/", StringComparison.OrdinalIgnoreCase);
}

public sealed record ApplicationUpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    Uri ReleasePage,
    DateTimeOffset CheckedAtUtc)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}
