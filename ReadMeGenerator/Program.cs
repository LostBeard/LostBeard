using System.Net;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReadMeGenerator.Models;
using ReadMeGenerator.Pages;
using ReadMeGenerator.Services;

// Resolve profile repo root (directory containing README.md / .git)
var repoRoot = FindRepoRoot(args);
Console.WriteLine($"Repo root: {repoRoot}");

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    // Local convenience: reuse gh auth when GITHUB_TOKEN is unset
    token = TryGhAuthToken();
}
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("GITHUB_TOKEN is required (or run `gh auth login` for local use).");
    return 1;
}

var services = new ServiceCollection();
services.AddLogging(b =>
{
    b.AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = "HH:mm:ss ";
    });
    b.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
});
services.AddSingleton<ReadmeData>();
services.AddHttpClient<GitHubApiClient>((_, http) => GitHubApiClient.Configure(http, token));
services.AddHttpClient<NuGetStatsService>();
services.AddTransient<GitHubRepoService>();
services.AddTransient<FundingService>();
services.AddTransient<ActivityTrackerService>();

await using var sp = services.BuildServiceProvider();
var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("ReadMeGenerator");
var data = sp.GetRequiredService<ReadmeData>();

logger.LogInformation("Fetching NuGet profile stats...");
var nuget = sp.GetRequiredService<NuGetStatsService>();
data.NuGet = await nuget.GetProfileStatsAsync();

logger.LogInformation("Fetching top repositories...");
var repos = sp.GetRequiredService<GitHubRepoService>();
data.TopRepos = await repos.GetTopReposAsync();

logger.LogInformation("Updating monthly funding bar...");
var funding = sp.GetRequiredService<FundingService>();
var fundingStats = await funding.GetMonthlyFundingStatsAsync();
await funding.WriteMonthlyFundingBarAsync(repoRoot, fundingStats);

logger.LogInformation("Updating hardware funding bar...");
var hardwareStats = await funding.GetHardwareFundingStatsAsync(repoRoot);
await funding.WriteHardwareFundingBarAsync(repoRoot, hardwareStats);

logger.LogInformation("Updating activity tracker...");
var activity = sp.GetRequiredService<ActivityTrackerService>();
await activity.WriteActivitySvgAsync(repoRoot);

logger.LogInformation("Rendering README.razor...");
await using var htmlRenderer = new HtmlRenderer(sp, sp.GetRequiredService<ILoggerFactory>());
var html = await htmlRenderer.Dispatcher.InvokeAsync(async () =>
{
    var output = await htmlRenderer.RenderComponentAsync<README>();
    return output.ToHtmlString();
});

var markdown = WebUtility.HtmlDecode(html)
    .Replace("\r\n", "\n")
    .Trim() + "\n";

var readmePath = Path.Combine(repoRoot, "README.md");
await File.WriteAllTextAsync(readmePath, markdown);
logger.LogInformation("Wrote {Path} ({Bytes} bytes)", readmePath, markdown.Length);
logger.LogInformation("Done.");
return 0;

static string FindRepoRoot(string[] args)
{
    if (args.Length > 0 && Directory.Exists(args[0]))
        return Path.GetFullPath(args[0]);

    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "README.md"))
            && Directory.Exists(Path.Combine(dir.FullName, ".github")))
            return dir.FullName;
        dir = dir.Parent;
    }

    // Fallback: project lives in ReadMeGenerator/ under the profile repo
    var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    if (File.Exists(Path.Combine(fromBase, "README.md")))
        return fromBase;

    return Directory.GetCurrentDirectory();
}

static string? TryGhAuthToken()
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "gh",
            Arguments = "auth token",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null)
            return null;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode == 0 ? output.Trim() : null;
    }
    catch
    {
        return null;
    }
}
