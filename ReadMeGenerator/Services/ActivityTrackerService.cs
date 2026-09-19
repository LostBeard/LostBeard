using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReadMeGenerator.Models;

namespace ReadMeGenerator.Services;

public sealed class ActivityTrackerService
{
    private readonly GitHubApiClient _api;
    private readonly ILogger<ActivityTrackerService> _logger;
    private const string Username = "LostBeard";
    private const int DaysCoverage = 28;
    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    public ActivityTrackerService(GitHubApiClient api, ILogger<ActivityTrackerService> logger)
    {
        _api = api;
        _logger = logger;
    }

    private static TimeZoneInfo ResolveEastern()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
    }

    public async Task WriteActivitySvgAsync(string repoRoot, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching commit activity for the last {Days} days...", DaysCoverage);
        var checkSince = DateTime.UtcNow.AddDays(-DaysCoverage);
        var repos = await GetRepositoriesAsync(ct);
        _logger.LogInformation("Found {Count} repositories. Scanning commits in parallel...", repos.Count);

        var commits = new ConcurrentBag<CommitInfo>();
        var scanned = 0;
        using var semaphore = new SemaphoreSlim(8);
        var tasks = repos.Select(async repo =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var repoCommits = await GetCommitDetailsAsync(repo, checkSince, ct);
                foreach (var c in repoCommits)
                    commits.Add(c);
            }
            finally
            {
                var current = Interlocked.Increment(ref scanned);
                if (current % 10 == 0 || current == repos.Count)
                    _logger.LogInformation(
                        "Progress: {Current}/{Total} repos... {CommitCount} commits",
                        current, repos.Count, commits.Count);
                semaphore.Release();
            }
        });
        await Task.WhenAll(tasks);

        if (commits.Count == 0)
        {
            _logger.LogWarning("No commits found in the last {Days} days; writing empty-state SVG", DaysCoverage);
        }
        else
        {
            _logger.LogInformation("Found {Count} total commits. Generating scatter SVG...", commits.Count);
        }

        var sorted = commits.OrderBy(c => c.Timestamp).ToList();
        var svg = BuildScatterSvg(sorted, checkSince);
        var assetsDir = Path.Combine(repoRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        var outPath = Path.Combine(assetsDir, "activity-tracker.svg");
        await File.WriteAllTextAsync(outPath, svg, ct);
        _logger.LogInformation("Wrote {Path}", outPath);
    }

    private async Task<List<string>> GetRepositoriesAsync(CancellationToken ct)
    {
        var repos = new List<string>();
        var page = 1;
        while (true)
        {
            // Authenticated /user/repos includes private repos the token can see
            var json = await _api.GetStringAsync(
                $"user/repos?per_page=100&page={page}&affiliation=owner", ct);
            using var doc = JsonDocument.Parse(json);
            var count = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                count++;
                if (element.TryGetProperty("fork", out var fork) && fork.GetBoolean())
                    continue;
                if (element.TryGetProperty("full_name", out var full))
                {
                    var name = full.GetString();
                    if (!string.IsNullOrEmpty(name))
                        repos.Add(name);
                }
            }
            if (count < 100)
                break;
            page++;
        }
        return repos;
    }

    private async Task<List<CommitInfo>> GetCommitDetailsAsync(
        string repo,
        DateTime since,
        CancellationToken ct)
    {
        var list = new List<CommitInfo>();
        var sinceIso = since.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var page = 1;

        while (true)
        {
            string? json;
            try
            {
                json = await _api.TryGetStringAsync(
                    $"repos/{repo}/commits?author={Username}&since={Uri.EscapeDataString(sinceIso)}&per_page=100&page={page}",
                    ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Skipping commits for {Repo}: {Message}", repo, ex.Message);
                break;
            }

            if (json is null)
                break;

            var trimmed = json.TrimStart();
            if (!trimmed.StartsWith('['))
                break;

            var commitsFound = 0;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var commitObj in doc.RootElement.EnumerateArray())
                {
                    commitsFound++;
                    if (commitObj.TryGetProperty("commit", out var commit)
                        && commit.TryGetProperty("author", out var author)
                        && author.TryGetProperty("date", out var dateProp))
                    {
                        var dateStr = dateProp.GetString();
                        if (dateStr != null
                            && DateTime.TryParse(dateStr, null, DateTimeStyles.RoundtripKind, out var date))
                        {
                            var eastern = TimeZoneInfo.ConvertTimeFromUtc(
                                date.Kind == DateTimeKind.Utc ? date : date.ToUniversalTime(),
                                Eastern);
                            list.Add(new CommitInfo
                            {
                                Timestamp = eastern,
                                RepoName = repo,
                            });
                        }
                    }
                }
            }
            catch (JsonException)
            {
                break;
            }

            if (commitsFound < 100)
                break;
            page++;
        }

        return list;
    }

    private static string BuildScatterSvg(List<CommitInfo> commits, DateTime sinceUtc)
    {
        const int paddingLeft = 60;
        const int paddingRight = 300;
        const int paddingTop = 50;
        const int paddingBottom = 60;
        const int chartWidth = 900;
        const int chartHeight = 550;
        const int totalWidth = chartWidth + paddingLeft + paddingRight;
        const int totalHeight = chartHeight + paddingTop + paddingBottom;

        var localSince = TimeZoneInfo.ConvertTimeFromUtc(sinceUtc, Eastern).Date;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Eastern);
        var localTo = localNow.Date + TimeSpan.FromDays(1);
        var totalDays = (localTo - localSince).TotalDays;
        if (totalDays <= 0)
            totalDays = 1;

        var repoCommitCounts = commits
            .GroupBy(c => c.RepoName)
            .ToDictionary(g => g.Key, g => g.Count());
        // Most active repos first so the legend prioritizes what matters
        var uniqueActiveRepos = repoCommitCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv => kv.Key)
            .ToList();
        var totalCommits = commits.Count;

        var svg = new StringBuilder();
        svg.AppendLine(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {totalWidth} {totalHeight}\" width=\"100%\" height=\"100%\" style=\"background-color:#0d1117;\">");

        svg.AppendLine("  <style>");
        svg.AppendLine("    .axis-line { stroke: #30363d; stroke-width: 1; }");
        svg.AppendLine("    .grid-line { stroke: #21262d; stroke-width: 1; stroke-dasharray: 4,4; }");
        svg.AppendLine(
            "    .text { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 12px; fill: #8b949e; }");
        svg.AppendLine(
            "    .legend-text { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 11px; fill: #c9d1d9; }");
        svg.AppendLine("    .title { font-size: 16px; font-weight: bold; fill: #c9d1d9; }");
        svg.AppendLine(
            "    .commit-dot { opacity: 0.75; transition: all 0.15s ease-in-out; stroke: #0d1117; stroke-width: 0.5; }");
        svg.AppendLine(
            "    .commit-dot:hover { opacity: 1; stroke: #ffffff; stroke-width: 1.5; r: 7px !important; }");
        svg.AppendLine("  </style>");

        svg.AppendLine(
            $"  <text x=\"{paddingLeft}\" y=\"28\" class=\"text title\">GitHub Commit Activity Tracker (Last {DaysCoverage} Days: {localSince:MM/dd} - {localTo:MM/dd}) - {totalCommits} commits</text>");

        for (var h = 0; h <= 24; h += 4)
        {
            var y = paddingTop + (int)((h / 24.0) * chartHeight);
            svg.AppendLine(
                $"  <line x1=\"{paddingLeft}\" y1=\"{y}\" x2=\"{paddingLeft + chartWidth}\" y2=\"{y}\" class=\"grid-line\" />");
            svg.AppendLine(
                $"  <text x=\"{paddingLeft - 10}\" y=\"{y + 4}\" class=\"text\" text-anchor=\"end\">{h:D2}:00</text>");
        }

        for (var d = 0; d <= totalDays; d += 2)
        {
            var x = paddingLeft + (int)((d / totalDays) * chartWidth);
            svg.AppendLine(
                $"  <line x1=\"{x}\" y1=\"{paddingTop}\" x2=\"{x}\" y2=\"{paddingTop + chartHeight}\" class=\"grid-line\" />");
            var dayLabel = localSince.AddDays(d).ToString("MM/dd");
            svg.AppendLine(
                $"  <text x=\"{x}\" y=\"{paddingTop + chartHeight + 20}\" class=\"text\" text-anchor=\"middle\">{dayLabel}</text>");
        }

        svg.AppendLine(
            $"  <line x1=\"{paddingLeft}\" y1=\"{paddingTop}\" x2=\"{paddingLeft}\" y2=\"{paddingTop + chartHeight}\" class=\"axis-line\" />");
        svg.AppendLine(
            $"  <line x1=\"{paddingLeft}\" y1=\"{paddingTop + chartHeight}\" x2=\"{paddingLeft + chartWidth}\" y2=\"{paddingTop + chartHeight}\" class=\"axis-line\" />");

        var coordinateGroups = new Dictionary<(int, int, int), int>();
        foreach (var c in commits)
        {
            var dayOffset = (c.Timestamp - localSince).TotalSeconds / 86400.0;
            var x = paddingLeft + (int)((dayOffset / totalDays) * chartWidth);
            var hourOffset = c.Timestamp.Hour + (c.Timestamp.Minute / 60.0) + (c.Timestamp.Second / 3600.0);
            var y = paddingTop + (int)((hourOffset / 24.0) * chartHeight);
            var snapX = (x / 2) * 2;
            var snapY = (y / 2) * 2;
            var repoIdx = uniqueActiveRepos.IndexOf(c.RepoName);
            var key = (snapX, snapY, repoIdx);
            if (coordinateGroups.ContainsKey(key))
                coordinateGroups[key]++;
            else
                coordinateGroups[key] = 1;
        }

        foreach (var pair in coordinateGroups)
        {
            var cx = pair.Key.Item1;
            var cy = pair.Key.Item2;
            var repoIdx = pair.Key.Item3;
            var count = pair.Value;
            var color = GetStableColor(repoIdx, uniqueActiveRepos.Count);
            var radius = 3.0 + Math.Min(5.0, count * 0.5);
            svg.AppendLine(
                $"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{radius:F1}\" fill=\"{color}\" class=\"commit-dot\"><title>[{uniqueActiveRepos[repoIdx]}] {count} commit(s) here</title></circle>");
        }

        var legendX = paddingLeft + chartWidth + 40;
        var legendYStart = paddingTop + 15;
        svg.AppendLine(
            $"  <text x=\"{legendX}\" y=\"{legendYStart - 15}\" class=\"text\" style=\"font-weight:bold; fill:#c9d1d9;\">ACTIVE REPOSITORIES ({uniqueActiveRepos.Count})</text>");

        for (var i = 0; i < uniqueActiveRepos.Count; i++)
        {
            var itemY = legendYStart + (i * 18);
            if (itemY > paddingTop + chartHeight)
            {
                svg.AppendLine(
                    $"  <text x=\"{legendX}\" y=\"{itemY}\" class=\"text\" style=\"font-style:italic;\">+ {uniqueActiveRepos.Count - i} more repos...</text>");
                break;
            }

            var color = GetStableColor(i, uniqueActiveRepos.Count);
            var repoName = uniqueActiveRepos[i];
            var repoCount = repoCommitCounts[repoName];
            var countSuffix = $" ({repoCount})";
            var maxNameLen = 35 - countSuffix.Length;
            if (maxNameLen < 8)
                maxNameLen = 8;
            var shortName = repoName;
            if (shortName.Length > maxNameLen)
                shortName = shortName[..Math.Max(0, maxNameLen - 3)] + "...";

            svg.AppendLine(
                $"  <circle cx=\"{legendX + 5}\" cy=\"{itemY - 4}\" r=\"5\" fill=\"{color}\" stroke=\"#0d1117\" stroke-width=\"0.5\" />");
            svg.AppendLine(
                $"  <text x=\"{legendX + 18}\" y=\"{itemY}\" class=\"legend-text\">{shortName}{countSuffix}</text>");
        }

        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static string GetStableColor(int index, int totalCount)
    {
        if (totalCount <= 0)
            totalCount = 1;
        var hue = (index * (360.0 / totalCount)) % 360;
        return $"hsl({hue:F1}, 85%, 60%)";
    }
}
