using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ReadMeGenerator.Models;

namespace ReadMeGenerator.Services;

public sealed partial class FundingService
{
    private readonly GitHubApiClient _api;
    private readonly ILogger<FundingService> _logger;
    private const double MonthlyGoalDollars = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public FundingService(GitHubApiClient api, ILogger<FundingService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<FundingStats> GetMonthlyFundingStatsAsync(CancellationToken ct = default)
    {
        const string query =
            """{"query":"query { viewer { sponsorshipsAsMaintainer(first: 100) { nodes { tier { monthlyPriceInCents } } } } }"}""";

        var json = await _api.PostJsonAsync("graphql", query, ct);
        var matches = MonthlyCentsRegex().Matches(json);
        double currentCents = matches.Sum(m => double.Parse(m.Groups[1].Value));
        var stats = new FundingStats
        {
            CurrentDollars = currentCents / 100.0,
            GoalDollars = MonthlyGoalDollars,
        };
        _logger.LogInformation(
            "Monthly funding: ${Current:N0} / ${Goal:N0} ({Percent:N1}%)",
            stats.CurrentDollars, stats.GoalDollars, stats.Percent);
        return stats;
    }

    /// <summary>
    /// Loads manually edited hardware fundraising totals from hardware-funding.json.
    /// Edit that file to update the amount received - there is no automated source yet.
    /// </summary>
    public async Task<FundingStats> GetHardwareFundingStatsAsync(string repoRoot, CancellationToken ct = default)
    {
        var path = ResolveHardwareConfigPath(repoRoot);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "hardware-funding.json not found. Create it with currentDollars and goalDollars.", path);

        await using var stream = File.OpenRead(path);
        var config = await JsonSerializer.DeserializeAsync<HardwareFundingConfig>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException($"Failed to parse {path}");

        var stats = new FundingStats
        {
            CurrentDollars = config.CurrentDollars,
            GoalDollars = config.GoalDollars,
        };
        _logger.LogInformation(
            "Hardware funding (manual): ${Current:N0} / ${Goal:N0} ({Percent:N1}%) from {Path}",
            stats.CurrentDollars, stats.GoalDollars, stats.Percent, path);
        return stats;
    }

    public Task WriteMonthlyFundingBarAsync(string repoRoot, FundingStats stats, CancellationToken ct = default) =>
        WriteProgressBarAsync(repoRoot, stats, "funding-bar.svg", ct);

    public Task WriteHardwareFundingBarAsync(string repoRoot, FundingStats stats, CancellationToken ct = default) =>
        WriteProgressBarAsync(repoRoot, stats, "hardware-funding-bar.svg", ct);

    private async Task WriteProgressBarAsync(
        string repoRoot,
        FundingStats stats,
        string fileName,
        CancellationToken ct)
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
        var outPath = Path.Combine(assetsDir, fileName);
        await File.WriteAllTextAsync(outPath, output, ct);
        _logger.LogInformation("Wrote {Path}", outPath);
    }

    private static string ResolveHardwareConfigPath(string repoRoot)
    {
        var inProject = Path.Combine(repoRoot, "ReadMeGenerator", "hardware-funding.json");
        if (File.Exists(inProject))
            return inProject;

        var besideOutput = Path.Combine(AppContext.BaseDirectory, "hardware-funding.json");
        if (File.Exists(besideOutput))
            return besideOutput;

        return inProject;
    }

    private sealed class HardwareFundingConfig
    {
        public double CurrentDollars { get; set; }
        public double GoalDollars { get; set; } = 4500;
    }

    [GeneratedRegex(@"""monthlyPriceInCents"":\s*(\d+)")]
    private static partial Regex MonthlyCentsRegex();
}
