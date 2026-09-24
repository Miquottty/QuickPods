using QuickPods.Core.Models;
using QuickPods.Presentation;
using Xunit;

namespace QuickPods.Foundation.Tests;

public sealed class ApplicationUpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AutomaticCheckRequiresOptInAndAStaleOrMissingTimestamp()
    {
        QuickPodsSettings disabled = QuickPodsSettings.Default;
        QuickPodsSettings neverChecked = disabled with { CheckForUpdatesAtStartup = true };
        QuickPodsSettings recent = neverChecked with { LastUpdateCheckUtc = Now.AddHours(-23) };
        QuickPodsSettings stale = neverChecked with { LastUpdateCheckUtc = Now.AddHours(-24) };

        Assert.False(ApplicationUpdatePolicy.ShouldCheckAutomatically(disabled, Now));
        Assert.True(ApplicationUpdatePolicy.ShouldCheckAutomatically(neverChecked, Now));
        Assert.False(ApplicationUpdatePolicy.ShouldCheckAutomatically(recent, Now));
        Assert.True(ApplicationUpdatePolicy.ShouldCheckAutomatically(stale, Now));
    }

    [Fact]
    public void FutureTimestampDoesNotSuppressAutomaticChecks()
    {
        QuickPodsSettings settings = QuickPodsSettings.Default with
        {
            CheckForUpdatesAtStartup = true,
            LastUpdateCheckUtc = Now.AddMinutes(1),
        };

        Assert.True(ApplicationUpdatePolicy.ShouldCheckAutomatically(settings, Now));
    }

    [Fact]
    public void CachedResultRequiresAnOfficialHttpsReleasePage()
    {
        QuickPodsSettings valid = QuickPodsSettings.Default with
        {
            LastUpdateCheckUtc = Now,
            LastKnownLatestVersion = "0.2.0",
            LastKnownReleasePage = "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0",
        };

        ApplicationUpdateCheckResult result = Assert.IsType<ApplicationUpdateCheckResult>(
            ApplicationUpdatePolicy.TryCreateCachedResult(valid, new Version(0, 1, 2)));
        Assert.True(result.IsUpdateAvailable);
        Assert.Null(ApplicationUpdatePolicy.TryCreateCachedResult(
            valid with { LastKnownReleasePage = "https://example.com/release" },
            new Version(0, 1, 2)));
    }

    [Fact]
    public void CachedReleasePageFromThePreviousOwnerIsDiscarded()
    {
        QuickPodsSettings settings = QuickPodsSettings.Default with
        {
            LastUpdateCheckUtc = Now,
            LastKnownLatestVersion = "1.0.2",
            LastKnownReleasePage = "https://github.com/rimtty/QuickPods/releases/tag/v1.0.2",
        };

        Assert.Null(ApplicationUpdatePolicy.TryCreateCachedResult(settings, new Version(1, 0, 2)));
    }
}
