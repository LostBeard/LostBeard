using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReadMeGenerator.Models;

namespace ReadMeGenerator.Services;

public sealed partial class FundingService
{
    private readonly GitHubApiClient _api;
    private readonly ILogger<FundingService> _logger;
    private const double GoalDollars = 500;

    public FundingService(GitHubApiClient api, ILogger<FundingService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<FundingStats> GetFundingStatsAsync(CancellationToken ct = default)
    {
        const string query =
            """{"query":"query { viewer { sponsorshipsAsMaintainer(first: 100) { nodes { tier { monthlyPriceInCents } } } } }"}""";

        var json = await _api.PostJsonAsync("graphql", query, ct);
        var matches = MonthlyCentsRegex().Matches(json);
        double currentCents = matches.Sum(m => double.Parse(m.Groups[1].Value));
        var stats = new FundingStats
        {
            CurrentDollars = currentCents / 100.0,
            GoalDollars = GoalDollars,
        };
        _logger.LogInformation(
            "Funding: ${Current:N0} / ${Goal:N0} ({Percent:N1}%)",
            stats.CurrentDollars, stats.GoalDollars, stats.Percent);
        return stats;
    }

    public async Task WriteFundingBarAsync(string repoRoot, FundingStats stats, CancellationToken ct = default)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "progress.svg");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(repoRoot, "ReadMeGenerator", "Templates", "progress.svg");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(repoRoot, "progress.svg");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("progress.svg template not found", templatePath);

        var template = await File.ReadAllTextAsync(templatePath, ct);
        var output = template
            .Replace("{PERCENT_WIDTH}", stats.PixelWidth.ToString())
            .Replace("{CURRENT}", stats.CurrentDollars.ToString("N0"))
            .Replace("{GOAL}", stats.GoalDollars.ToString("N0"))
            .Replace("{PERCENT}", stats.Percent.ToString("N1"));

        var assetsDir = Path.Combine(repoRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        var outPath = Path.Combine(assetsDir, "funding-bar.svg");
        await File.WriteAllTextAsync(outPath, output, ct);
        _logger.LogInformation("Wrote {Path}", outPath);
    }

    [GeneratedRegex(@"""monthlyPriceInCents"":\s*(\d+)")]
    private static partial Regex MonthlyCentsRegex();
}
