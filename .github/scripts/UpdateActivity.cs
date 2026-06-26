using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Program
{
    class CommitInfo
    {
        public DateTime Timestamp { get; set; }
        public string RepoName { get; set; } = string.Empty;
    }

    static async Task Main(string[] args)
    {
        int daysCoverage = 28;
        Console.WriteLine($"Starting GitHub commit activity tracking for the past {daysCoverage} days...");

        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            Console.WriteLine("Error: GITHUB_TOKEN environment variable is missing.");
            Environment.Exit(1);
        }

        // Get the repository owner/actor context from the runner environment
        string owner = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER") ?? "";
        if (string.IsNullOrEmpty(owner))
        {
            Console.WriteLine("Error: GITHUB_REPOSITORY_OWNER context unavailable.");
            Environment.Exit(1);
        }
        Console.WriteLine($"Target Profile Context: {owner}");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpawnDev-Activity-Tracker", "1.0"));

        // Fetch repositories for this user via the REST API
        var repos = await GetUserRepositories(client, owner);
        Console.WriteLine($"Discovered {repos.Count} repositories. Pulling activity data...");

        DateTime checkSince = DateTime.UtcNow.AddDays(-daysCoverage);
        var commits = new ConcurrentBag<CommitInfo>();
        int scanned = 0;

        // Process repositories in parallel inside the runner
        using (var semaphore = new SemaphoreSlim(8))
        {
            var tasks = repos.Select(async repo =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Use the gh CLI directly in the runner to check all branches
                    // The gh CLI automatically picks up the GITHUB_TOKEN environment variable
                    string args = $"log --since=\"{checkSince:yyyy-MM-ddTHH:mm:ssZ}\" --all --date=iso-strict --format=\"%ad\" -R {owner}/{repo}";
                    string output = await RunGhCommandAsync(args);

                    if (!string.IsNullOrEmpty(output))
                    {
                        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            if (DateTime.TryParse(line, null, DateTimeStyles.RoundtripKind, out DateTime dt))
                            {
                                commits.Add(new CommitInfo { Timestamp = dt.ToUniversalTime(), RepoName = repo });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed scanning history for {repo}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref scanned);
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        Console.WriteLine($"Processing complete. Extracted total commit data data points.");
        
        // Generate and save the standalone SVG file
        GenerateSvgGrid(commits.ToList(), daysCoverage);
    }

    static async Task<List<string>> GetUserRepositories(HttpClient client, string owner)
    {
        var repoList = new List<string>();
        int page = 1;
        while (true)
        {
            // Fetch both public and private repositories accessible by the token
            string url = $"https://api.github.com/user/repos?per_page=100&page={page}&type=owner";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode) break;

            string json = await response.Content.ReadAsStringAsync();
            var matches = Regex.Matches(json, @"""name"":\s*""([^""]+)""");
            if (matches.Count == 0) break;

            foreach (Match match in matches)
            {
                string name = match.Groups[1].Value;
                if (!repoList.Contains(name)) repoList.Add(name);
            }
            page++;
        }
        return repoList;
    }

    static void GenerateSvgGrid(List<CommitInfo> commits, int daysCoverage)
    {
        // -------------------------------------------------------------
        // SVG Vector Geometry Definition 
        // -------------------------------------------------------------
        int cellWidth = 12;
        int cellHeight = 12;
        int padding = 4;
        
        int headerHeight = 50;
        int leftAxisWidth = 60;
        int legendWidth = 180;
        
        int gridColumns = daysCoverage;
        int gridRows = 24; // 24 hours
        
        int gridWidth = gridColumns * (cellWidth + padding);
        int gridHeight = gridRows * (cellHeight + padding);
        
        int svgWidth = leftAxisWidth + gridWidth + legendWidth + 40;
        int svgHeight = headerHeight + gridHeight + 40;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{svgWidth}\" height=\"{svgHeight}\" viewBox=\"0 0 {svgWidth} {svgHeight}\">");
        sb.AppendLine("<style>");
        sb.AppendLine("  .title { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 16px; fill: #adbac7; font-weight: 600; }");
        sb.AppendLine("  .axis-label { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 10px; fill: #768390; }");
        sb.AppendLine("  .legend-text { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 11px; fill: #adbac7; }");
        sb.AppendLine("</style>");
        
        // Background palette matching GitHub dark mode theme
        sb.AppendLine($"<rect width=\"100%\" height=\"100%\" fill=\"#1c2128\" rx=\"6\"/>");

        // Header Title
        DateTime startDate = DateTime.UtcNow.AddDays(-daysCoverage + 1);
        DateTime endDate = DateTime.UtcNow;
        sb.AppendLine($"<text x=\"20\" y=\"30\" class=\"title\">GitHub Commit Activity Tracker (Last {daysCoverage} Days: {startDate:MM/dd}-{endDate:MM/dd})</text>");

        // Render Y-Axis (Hours)
        for (int hour = 0; hour < 24; hour += 4)
        {
            int yPos = headerHeight + (hour * (cellHeight + padding)) + 10;
            sb.AppendLine($"<text x=\"15\" y=\"{yPos}\" class=\"axis-label\">{hour:D2}:00</text>");
        }

        // Aggregate and map commit frequencies per bucket (Day index, Hour)
        var uniqueRepos = commits.Select(c => c.RepoName).Distinct().ToList();
        var commitMap = new Dictionary<(int dayOffset, int hour), (int count, string leadRepo)>();

        var groupedCommits = commits
            .GroupBy(c => (DayOffset: (DateTime.UtcNow.Date - c.Timestamp.Date).Days, c.Timestamp.Hour))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Process metrics inside the layout loop
        for (int col = 0; col < gridColumns; col++)
        {
            int dayOffset = (gridColumns - 1) - col; 
            DateTime currentTargetDate = DateTime.UtcNow.AddDays(-dayOffset);
            
            // X location for tracking column items
            int xPos = leftAxisWidth + (col * (cellWidth + padding));

            // Draw date label headers on intervals to prevent crowding
            if (col % 4 == 0 || col == gridColumns - 1)
            {
                sb.AppendLine($"<text x=\"{xPos}\" y=\"{headerHeight - 8}\" class=\"axis-label\">{currentTargetDate:MM/dd}</text>");
            }

            for (int hour = 0; hour < 24; hour++)
            {
                int yPos = headerHeight + (hour * (cellHeight + padding));
                string color = "#22272e"; // Default empty block color
                string tooltip = $"{currentTargetDate:yyyy-MM-dd} {hour:D2}:00 - 0 commits";

                if (groupedCommits.TryGetValue((dayOffset, hour), out var list) && list.Count > 0)
                {
                    var primaryRepo = list.GroupBy(c => c.RepoName)
                                          .OrderByDescending(g => g.Count())
                                          .First().Key;
                    
                    int repoIndex = uniqueRepos.IndexOf(primaryRepo);
                    color = GetColorPaletteHsl(repoIndex, uniqueRepos.Count);
                    tooltip = $"{currentTargetDate:yyyy-MM-dd} {hour:D2}:00 - {list.Count} commit(s) [{primaryRepo}]";
                }

                sb.AppendLine($"  <rect x=\"{xPos}\" y=\"{yPos}\" width=\"{cellWidth}\" height=\"{cellHeight}\" fill=\"{color}\" rx=\"2\">");
                sb.AppendLine($"    <title>{tooltip}</title>");
                sb.AppendLine($"  </rect>");
            }
        }

        // Render Sidebar Active Repository Legend
        int legendX = leftAxisWidth + gridWidth + 25;
        int legendYStart = headerHeight + 10;
        sb.AppendLine($"<text x=\"{legendX}\" y=\"{legendYStart - 15}\" class=\"legend-text\" style=\"font-weight:600;\">ACTIVE REPOS ({uniqueRepos.Count})</text>");

        for (int i = 0; i < uniqueRepos.Count; i++)
        {
            int itemY = legendYStart + (i * 18);
            if (itemY > svgHeight - 20) break; // Hard bounds check

            string repoColor = GetColorPaletteHsl(i, uniqueRepos.Count);
            sb.AppendLine($"  <rect x=\"{legendX}\" y=\"{itemY}\" width=\"10\" height=\"10\" fill=\"{repoColor}\" rx=\"2\"/>");
            
            string nameClean = uniqueRepos[i].Length > 18 ? uniqueRepos[i].Substring(0, 15) + "..." : uniqueRepos[i];
            sb.AppendLine($"  <text x=\"{legendX + 16}\" y=\"{itemY + 9}\" class=\"axis-label\" fill=\"#adbac7\">{nameClean}</text>");
        }

        sb.AppendLine("</svg>");

        // Match your structural output pathing patterns
        string outputDirectory = "assets";
        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        File.WriteAllText(Path.Combine(outputDirectory, "activity-tracker.svg"), sb.ToString(), Encoding.UTF8);
        Console.WriteLine("Vector asset updated successfully: assets/activity-tracker.svg");
    }

    static string GetColorPaletteHsl(int index, int totalCount)
    {
        if (totalCount <= 0) totalCount = 1;
        double hue = (index * (360.0 / totalCount)) % 360;
        return $"hsl({hue:F1}, 85%, 60%)";
    }

    static async Task<string> RunGhCommandAsync(string arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = "gh";
        process.StartInfo.Arguments = arguments;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0 ? output : string.Empty;
    }
}