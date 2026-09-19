using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReadMeGenerator.Models;

namespace ReadMeGenerator.Services;

public sealed partial class GitHubRepoService
{
    private readonly GitHubApiClient _api;
    private readonly ILogger<GitHubRepoService> _logger;
    private const string Username = "LostBeard";
    private const int MaxRepos = 20;

    public GitHubRepoService(GitHubApiClient api, ILogger<GitHubRepoService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RepoPin>> GetTopReposAsync(CancellationToken ct = default)
    {
        var repos = new List<JsonElement>();
        var page = 1;
        while (true)
        {
            var json = await _api.GetStringAsync(
                $"users/{Username}/repos?sort=pushed&per_page=100&page={page}&type=owner", ct);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.EnumerateArray().ToList();
            if (arr.Count == 0)
                break;
            repos.AddRange(arr.Select(e => e.Clone()));
            if (arr.Count < 100)
                break;
            page++;
        }

        var top = repos
            .Where(r => !r.GetProperty("fork").GetBoolean())
            .OrderByDescending(r => r.GetProperty("stargazers_count").GetInt32())
            .Take(MaxRepos)
            .ToList();

        _logger.LogInformation("Building pins for top {Count} repos by stars", top.Count);

        var pins = new List<RepoPin>();
        foreach (var r in top)
        {
            ct.ThrowIfCancellationRequested();
            var name = r.GetProperty("name").GetString()!;
            var url = r.GetProperty("html_url").GetString()!;
            var desc = r.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "No description provided."
                : "No description provided.";
            desc = desc.Replace('\n', ' ').Replace('\r', ' ');
            if (desc.Length > 120)
                desc = desc[..117] + "...";

            var badges = await GetNuGetBadgesAsync(name, ct);
            pins.Add(new RepoPin
            {
                Name = name,
                Url = url,
                Description = desc,
                Stars = r.GetProperty("stargazers_count").GetInt32(),
                Forks = r.GetProperty("forks_count").GetInt32(),
                NuGetBadges = badges,
            });
        }

        return pins;
    }

    private async Task<IReadOnlyList<NuGetBadge>> GetNuGetBadgesAsync(string repoName, CancellationToken ct)
    {
        var json = await _api.TryGetStringAsync($"repos/{Username}/{repoName}/readme", ct);
        if (json is null)
            return [];

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("content", out var contentProp))
            return [];

        var b64 = contentProp.GetString()?.Replace("\n", "") ?? "";
        var readme = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        return ExtractNuGetBadges(readme);
    }

    private static List<NuGetBadge> ExtractNuGetBadges(string readme)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var badges = new List<NuGetBadge>();
        foreach (Match match in NuGetUrlRegex().Matches(readme))
        {
            var packageId = match.Groups[1].Value;
            if (!IsSpawnDevPackage(packageId))
                continue;
            if (!seen.Add(packageId))
                continue;
            badges.Add(new NuGetBadge
            {
                PackageId = packageId,
                PackageUrl = $"https://www.nuget.org/packages/{packageId}",
            });
        }
        return badges;
    }

    private static bool IsSpawnDevPackage(string packageId) =>
        packageId.Equals("SpawnDev", StringComparison.OrdinalIgnoreCase)
        || packageId.StartsWith("SpawnDev.", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"https://www\.nuget\.org/packages/([A-Za-z0-9._-]+)(?:/[A-Za-z0-9._-]+)?/?", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetUrlRegex();
}
