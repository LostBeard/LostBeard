namespace ReadMeGenerator.Models;

public sealed class NuGetProfileStats
{
    public int PackageCount { get; init; }
    public long TotalDownloads { get; init; }

    /// <summary>Rounded-down thousands with + suffix, e.g. 523456 -> "523,000+".</summary>
    public string FormattedDownloads
    {
        get
        {
            if (TotalDownloads < 1_000)
                return TotalDownloads.ToString("N0");
            var rounded = (TotalDownloads / 1_000) * 1_000;
            return $"{rounded:N0}+";
        }
    }
}
