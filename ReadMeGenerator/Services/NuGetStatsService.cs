using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReadMeGenerator.Models;

namespace ReadMeGenerator.Services;

public sealed class NuGetStatsService
{
    private readonly HttpClient _http;
    private readonly ILogger<NuGetStatsService> _logger;
    private const string Owner = "LostBeard";

    public NuGetStatsService(HttpClient http, ILogger<NuGetStatsService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<NuGetProfileStats> GetProfileStatsAsync(CancellationToken ct = default)
    {
        // NuGet Search Query Service - owner filter returns this publisher's packages
        const int take = 100;
        var skip = 0;
        long totalDownloads = 0;
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int? reportedTotal = null;

        while (true)
        {
            var url =
                $"https://azuresearch-usnc.nuget.org/query?q=owner:{Owner}&skip={skip}&take={take}&prerelease=true&semVerLevel=2.0.0";
            _logger.LogInformation("Fetching NuGet packages skip={Skip}", skip);

            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            reportedTotal ??= doc.RootElement.TryGetProperty("totalHits", out var hits)
                ? hits.GetInt32()
                : null;

            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                break;

            foreach (var pkg in data.EnumerateArray())
            {
                var id = pkg.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id))
                    continue;
                if (!packageIds.Add(id))
                    continue;

                if (pkg.TryGetProperty("totalDownloads", out var dl))
                    totalDownloads += dl.GetInt64();
            }

            skip += take;
            if (reportedTotal is int total && skip >= total)
                break;
            if (data.GetArrayLength() < take)
                break;
        }

        var stats = new NuGetProfileStats
        {
            PackageCount = packageIds.Count,
            TotalDownloads = totalDownloads,
        };
        _logger.LogInformation(
            "NuGet profile: {Count} packages, {Downloads:N0} total downloads",
            stats.PackageCount, stats.TotalDownloads);
        return stats;
    }
}
