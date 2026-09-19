namespace ReadMeGenerator.Models;

public sealed class CommitInfo
{
    public DateTime Timestamp { get; init; }
    public required string RepoName { get; init; }
}
