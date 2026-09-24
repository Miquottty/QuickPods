using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using QuickPods.Infrastructure.Updates;
using Xunit;

namespace QuickPods.Foundation.Tests;

public sealed class GitHubReleaseUpdateCheckerTests
{
    [Fact]
    public async Task NewerStableReleaseIsReportedFromTheOfficialReleasePage()
    {
        string? requestUri = null;
        string? accept = null;
        string? userAgent = null;
        string? apiVersion = null;
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri?.AbsoluteUri;
            accept = request.Headers.Accept.Single().MediaType;
            userAgent = request.Headers.UserAgent.ToString();
            apiVersion = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            return CreateJsonResponse(
                "v0.2.0",
                "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0");
        }));
        var checker = new GitHubReleaseUpdateChecker(client);

        var result = await checker.CheckAsync(new Version(0, 1, 2, 0));

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(0, 1, 2), result.CurrentVersion);
        Assert.Equal(new Version(0, 2, 0), result.LatestVersion);
        Assert.Equal(
            "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0",
            result.ReleasePage.AbsoluteUri);
        Assert.Equal(
            "https://api.github.com/repos/Miquottty/QuickPods/releases/latest",
            requestUri);
        Assert.Equal("application/vnd.github+json", accept);
        Assert.Equal("QuickPods/0.1.2", userAgent);
        Assert.Equal("2026-03-10", apiVersion);
    }

    [Fact]
    public async Task MatchingThreePartReleaseIsUpToDateForFourPartAssemblyVersion()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => CreateJsonResponse(
            "v0.1.2",
            "https://github.com/Miquottty/QuickPods/releases/tag/v0.1.2")));
        var checker = new GitHubReleaseUpdateChecker(client);

        var result = await checker.CheckAsync(new Version(0, 1, 2, 0));

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(result.CurrentVersion, result.LatestVersion);
    }

    [Theory]
    [InlineData("0.2", "https://github.com/Miquottty/QuickPods/releases/tag/v0.2")]
    [InlineData("v0.2.0", "https://example.com/Miquottty/QuickPods/releases/tag/v0.2.0")]
    [InlineData("v0.2.0", "http://github.com/Miquottty/QuickPods/releases/tag/v0.2.0")]
    [InlineData("v0.2.0", "https://github.com/rimtty/QuickPods/releases/tag/v0.2.0")]
    [InlineData("v0.2.0", "https://github.com/Miquottty/QuickPodsFork/releases/tag/v0.2.0")]
    public async Task InvalidReleaseMetadataIsRejected(string tag, string releasePage)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            CreateJsonResponse(tag, releasePage)));
        var checker = new GitHubReleaseUpdateChecker(client);

        _ = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await checker.CheckAsync(new Version(0, 1, 2)));
    }

    [Fact]
    public async Task DraftOrPrereleaseResponseIsRejected()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            CreateJsonResponse(
                "v0.2.0",
                "https://github.com/Miquottty/QuickPods/releases/tag/v0.2.0",
                prerelease: true)));
        var checker = new GitHubReleaseUpdateChecker(client);

        _ = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await checker.CheckAsync(new Version(0, 1, 2)));
    }

    private static HttpResponseMessage CreateJsonResponse(
        string tag,
        string releasePage,
        bool prerelease = false) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "tag_name": "{{tag}}",
                  "html_url": "{{releasePage}}",
                  "draft": false,
                  "prerelease": {{prerelease.ToString().ToLowerInvariant()}}
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
