using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using QuickPods.Core.Models;
using QuickPods.Core.Ports;

namespace QuickPods.Infrastructure.Updates;

public sealed class GitHubReleaseUpdateChecker : IApplicationUpdateChecker
{
    internal static readonly Uri LatestReleaseEndpoint = QuickPodsRepository.LatestReleaseApi;
    private const string GitHubApiVersion = "2026-03-10";

    private readonly HttpClient httpClient;

    public GitHubReleaseUpdateChecker(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async ValueTask<ApplicationUpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        Version normalizedCurrent = NormalizeVersion(currentVersion);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
            "QuickPods",
            normalizedCurrent.ToString(3)));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);

        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        GitHubReleaseResponse? release = await response.Content.ReadFromJsonAsync<GitHubReleaseResponse>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease)
        {
            throw new InvalidDataException("GitHub did not return a published stable release.");
        }

        string tag = release.TagName?.Trim() ?? string.Empty;
        if (tag.StartsWith('v') || tag.StartsWith('V'))
        {
            tag = tag[1..];
        }

        if (!Version.TryParse(tag, out Version? parsedVersion) ||
            parsedVersion.Build < 0 ||
            parsedVersion.Revision >= 0)
        {
            throw new InvalidDataException("The latest QuickPods release tag is invalid.");
        }

        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out Uri? releasePage) ||
            !QuickPodsRepository.IsReleasePage(releasePage))
        {
            throw new InvalidDataException("The latest QuickPods release URL is invalid.");
        }

        return new ApplicationUpdateCheckResult(
            normalizedCurrent,
            NormalizeVersion(parsedVersion),
            releasePage,
            DateTimeOffset.UtcNow);
    }

    private static Version NormalizeVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build));

    private sealed record GitHubReleaseResponse(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease);
}
