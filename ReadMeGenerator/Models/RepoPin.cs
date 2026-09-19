namespace ReadMeGenerator.Models;

public sealed class RepoPin
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public required string Description { get; init; }
    public int Stars { get; init; }
    public int Forks { get; init; }
    public IReadOnlyList<NuGetBadge> NuGetBadges { get; init; } = [];
}

public sealed class NuGetBadge
{
    public required string PackageId { get; init; }
    public required string PackageUrl { get; init; }

    public string BadgeMarkdown =>
        $"[![{PackageId}](https://img.shields.io/nuget/dt/{PackageId}.svg?label={PackageId})]({PackageUrl})";
}
