using QuickPods.Core.Models;

namespace QuickPods.Presentation;

public static class ApplicationUpdatePolicy
{
    public static TimeSpan AutomaticCheckInterval { get; } = TimeSpan.FromHours(24);

    public static bool ShouldCheckAutomatically(
        QuickPodsSettings settings,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.CheckForUpdatesAtStartup)
        {
            return false;
        }

        return settings.LastUpdateCheckUtc is not { } lastChecked ||
            lastChecked > utcNow ||
            utcNow - lastChecked >= AutomaticCheckInterval;
    }

    public static ApplicationUpdateCheckResult? TryCreateCachedResult(
        QuickPodsSettings settings,
        Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (settings.LastUpdateCheckUtc is not { } checkedAt ||
            !Version.TryParse(settings.LastKnownLatestVersion, out Version? latestVersion) ||
            !Uri.TryCreate(settings.LastKnownReleasePage, UriKind.Absolute, out Uri? releasePage) ||
            !QuickPodsRepository.IsReleasePage(releasePage))
        {
            return null;
        }

        return new ApplicationUpdateCheckResult(
            currentVersion,
            latestVersion,
            releasePage,
            checkedAt);
    }
}
