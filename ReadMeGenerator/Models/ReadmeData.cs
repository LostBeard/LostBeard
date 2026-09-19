namespace ReadMeGenerator.Models;

/// <summary>
/// Shared state populated before HtmlRenderer runs README.razor.
/// </summary>
public sealed class ReadmeData
{
    public NuGetProfileStats NuGet { get; set; } = new();
    public IReadOnlyList<RepoPin> TopRepos { get; set; } = [];
}
