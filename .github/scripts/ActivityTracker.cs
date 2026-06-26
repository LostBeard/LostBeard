// This script is meant to be ran on a local PC and it uses the gh comand line tool with the logged in gh account
// It upadtes the commit activity svg in the ../../assets folder which is then shown on the profile readme
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

class Program
{
    // A structured class to keep track of commit details for accurate color-coding
    class CommitInfo
    {
        public DateTime Timestamp { get; set; }
        public string RepoName { get; set; } = string.Empty;
    }

    static async Task Main(string[] args)
    {
        // 1. INCREASE COVERAGE: Expanded to 28 days (4 weeks)
        int daysCoverage = 28;
        Console.WriteLine($"Fetching GitHub activity for the last {daysCoverage} days...");
        
        DateTime checkSince = DateTime.UtcNow.AddDays(-daysCoverage);
        var commits = new ConcurrentBag<CommitInfo>();

        string username = GetGitHubUsername();
        if (string.IsNullOrEmpty(username))
        {
            Console.WriteLine("Error: Could not determine your GitHub username. Run 'gh auth status'.");
            return;
        }
        Console.WriteLine($"Authenticated as: {username}");

        var repos = GetRepositories();
        Console.WriteLine($"Found {repos.Count} repositories. Scanning all branches in parallel...");

        // 2. Parallelized multi-page tracking
        int scanned = 0;
        using (var semaphore = new SemaphoreSlim(8))
        {
            var tasks = repos.Select(async repo =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var repoCommits = await GetCommitDetailsAsync(repo, username, checkSince);
                    foreach (var c in repoCommits)
                    {
                        commits.Add(c);
                    }
                }
                finally
                {
                    int current = Interlocked.Increment(ref scanned);
                    Console.Write($"\rProgress: {current}/{repos.Count} repos checked... Found {commits.Count} commits.");
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        Console.WriteLine();

        if (commits.Count == 0)
        {
            Console.WriteLine($"\nNo commits found in the last {daysCoverage} days.");
            return;
        }

        Console.WriteLine($"\nFound {commits.Count} total commits. Generating plots...\n");

        var sortedCommits = commits.OrderBy(c => c.Timestamp).ToList();

        // 3. Render Console Plot (Using standalone timestamps)
        PlotActivityConsole(sortedCommits.Select(c => c.Timestamp).ToList(), checkSince, daysCoverage);

        // 4. Generate and save the advanced multi-color SVG
        SaveActivitySvg(sortedCommits, checkSince, daysCoverage);
    }

    static string GetGitHubUsername()
    {
        var output = RunGhCommand("api user --jq .login");
        return output?.Trim() ?? string.Empty;
    }

    static List<string> GetRepositories()
    {
        var repos = new List<string>();
        var output = RunGhCommand("repo list --limit 1000 --json nameWithOwner");
        
        if (string.IsNullOrWhiteSpace(output)) return repos;

        using var doc = JsonDocument.Parse(output);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("nameWithOwner", out var nameProp))
            {
                var repoName = nameProp.GetString();
                if (repoName != null) repos.Add(repoName);
            }
        }
        return repos;
    }

    static async Task<List<CommitInfo>> GetCommitDetailsAsync(string repo, string username, DateTime since)
    {
        var list = new List<CommitInfo>();
        string sinceIso = since.ToString("yyyy-MM-ddTHH:mm:ssZ");
        
        int page = 1;
        while (true)
        {
            string cmd = $"api repos/{repo}/commits?author={username}&since={sinceIso}&per_page=100&all=true&page={page}";
            var output = await RunGhCommandAsync(cmd);

            if (string.IsNullOrWhiteSpace(output)) break;

            string trimmed = output.Trim();
            if (!trimmed.StartsWith("[")) break;

            int commitsFound = 0;
            try
            {
                using var doc = JsonDocument.Parse(output);
                foreach (var commitObj in doc.RootElement.EnumerateArray())
                {
                    commitsFound++;
                    if (commitObj.TryGetProperty("commit", out var commit) &&
                        commit.TryGetProperty("author", out var author) &&
                        author.TryGetProperty("date", out var dateProp))
                    {
                        var dateStr = dateProp.GetString();
                        if (dateStr != null && DateTime.TryParse(dateStr, null, DateTimeStyles.RoundtripKind, out DateTime date))
                        {
                            list.Add(new CommitInfo 
                            { 
                                Timestamp = date.ToLocalTime(), 
                                RepoName = repo 
                            });
                        }
                    }
                }
            }
            catch
            {
                break;
            }

            if (commitsFound < 100) break;
            page++;
        }

        return list;
    }

    static void PlotActivityConsole(List<DateTime> timestamps, DateTime since, int daysCoverage)
    {
        int width = 70;  
        int height = 24; 

        char[,] grid = new char[height, width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                grid[y, x] = ' ';

        DateTime localSince = since.ToLocalTime().Date;
        DateTime localTo = DateTime.Now.Date;
        double totalDays = (localTo - localSince).TotalDays;
        if (totalDays <= 0) totalDays = 1;

        foreach (var time in timestamps)
        {
            double dayOffset = (time.Date - localSince).TotalDays;
            int x = (int)((dayOffset / totalDays) * (width - 1));
            int y = time.Hour; 

            if (x >= 0 && x < width && y >= 0 && y < height)
            {
                grid[y, x] = grid[y, x] == ' ' ? '•' : '☼'; 
            }
        }

        Console.WriteLine($"   Time of Day (Y) vs Last {daysCoverage} Days (X)");
        Console.WriteLine("   " + new string('-', width + 2));

        for (int h = 0; h < height; h++)
        {
            Console.Write($"{h:D2}:00 |");
            for (int w = 0; w < width; w++) Console.Write(grid[h, w]);
            Console.WriteLine("|");
        }

        Console.WriteLine("   " + new string('-', width + 2));
        
        string startLabel = localSince.ToString("MM/dd");
        string endLabel = localTo.ToString("MM/dd");
        Console.Write("    " + startLabel);
        Console.Write(new string(' ', Math.Max(1, width - startLabel.Length - endLabel.Length + 2)));
        Console.WriteLine(endLabel);
    }

    static void SaveActivitySvg(List<CommitInfo> commits, DateTime since, int daysCoverage)
    {
        // this writes the output to the assets folder in this repo
        string filename = $"../../assets/activity-tracker.svg";
        
        // Layout Dimensions (Expanded chart right-margin to fit a clean Legend side-panel)
        int paddingLeft = 60;
        int paddingRight = 320; // Extra room for the Repo Legend text
        int paddingTop = 50;
        int paddingBottom = 60;
        int chartWidth = 900;
        int chartHeight = 550;
        int totalWidth = chartWidth + paddingLeft + paddingRight;
        int totalHeight = chartHeight + paddingTop + paddingBottom;

        DateTime localSince = since.ToLocalTime().Date;
        DateTime localTo = DateTime.Now.Date;
        double totalDays = (localTo - localSince).TotalDays;
        if (totalDays <= 0) totalDays = 1;

        // Extract active unique repositories from found commits to color-code them
        var uniqueActiveRepos = commits.Select(c => c.RepoName).Distinct().OrderBy(r => r).ToList();

        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {totalWidth} {totalHeight}\" width=\"100%\" height=\"100%\" style=\"background-color:#0d1117;\">");
        
        svg.AppendLine("  <style>");
        svg.AppendLine("    .axis-line { stroke: #30363d; stroke-width: 1; }");
        svg.AppendLine("    .grid-line { stroke: #21262d; stroke-width: 1; stroke-dasharray: 4,4; }");
        svg.AppendLine("    .text { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 12px; fill: #8b949e; }");
        svg.AppendLine("    .legend-text { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif; font-size: 11px; fill: #c9d1d9; }");
        svg.AppendLine("    .title { font-size: 16px; font-weight: bold; fill: #c9d1d9; }");
        svg.AppendLine("    .commit-dot { opacity: 0.75; transition: all 0.15s ease-in-out; stroke: #0d1117; stroke-width: 0.5; }");
        svg.AppendLine("    .commit-dot:hover { opacity: 1; stroke: #ffffff; stroke-width: 1.5; r: 7px !important; }");
        svg.AppendLine("  </style>");

        svg.AppendLine($"  <text x=\"{paddingLeft}\" y=\"28\" class=\"text title\">GitHub Commit Activity Tracker (Last {daysCoverage} Days: {localSince:MM/dd} - {localTo:MM/dd})</text>");

        // Horizontal Gridlines (Hours - Steps of 4)
        for (int h = 0; h <= 24; h += 4)
        {
            int y = paddingTop + (int)((h / 24.0) * chartHeight);
            svg.AppendLine($"  <line x1=\"{paddingLeft}\" y1=\"{y}\" x2=\"{paddingLeft + chartWidth}\" y2=\"{y}\" class=\"grid-line\" />");
            svg.AppendLine($"  <text x=\"{paddingLeft - 10}\" y=\"{y + 4}\" class=\"text\" text-anchor=\"end\">{h:D2}:00</text>");
        }

        // Vertical Gridlines (Days - Labeling every 2 days for the 4-week density look)
        for (int d = 0; d <= totalDays; d += 2)
        {
            int x = paddingLeft + (int)((d / totalDays) * chartWidth);
            svg.AppendLine($"  <line x1=\"{x}\" y1=\"{paddingTop}\" x2=\"{x}\" y2=\"{paddingTop + chartHeight}\" class=\"grid-line\" />");
            string dayLabel = localSince.AddDays(d).ToString("MM/dd");
            svg.AppendLine($"  <text x=\"{x}\" y=\"{paddingTop + chartHeight + 20}\" class=\"text\" text-anchor=\"middle\">{dayLabel}</text>");
        }

        // Draw Axes
        svg.AppendLine($"  <line x1=\"{paddingLeft}\" y1=\"{paddingTop}\" x2=\"{paddingLeft}\" y2=\"{paddingTop + chartHeight}\" class=\"axis-line\" />");
        svg.AppendLine($"  <line x1=\"{paddingLeft}\" y1=\"{paddingTop + chartHeight}\" x2=\"{paddingLeft + chartWidth}\" y2=\"{paddingTop + chartHeight}\" class=\"axis-line\" />");

        // Group overlapping coordinates for perfect stacking metrics
        // Key: (SnapX, SnapY, RepoIndex) -> Count
        var coordinateGroups = new Dictionary<(int, int, int), int>();
        
        foreach (var c in commits)
        {
            double dayOffset = (c.Timestamp - localSince).TotalSeconds / 86400.0;
            int x = paddingLeft + (int)((dayOffset / totalDays) * chartWidth);
            
            double hourOffset = (c.Timestamp.Hour) + (c.Timestamp.Minute / 60.0) + (c.Timestamp.Second / 3600.0);
            int y = paddingTop + (int)((hourOffset / 24.0) * chartHeight);

            int snapX = (x / 2) * 2;
            int snapY = (y / 2) * 2;
            int repoIdx = uniqueActiveRepos.IndexOf(c.RepoName);

            var key = (snapX, snapY, repoIdx);
            if (coordinateGroups.ContainsKey(key)) coordinateGroups[key]++;
            else coordinateGroups[key] = 1;
        }

        // Plot Color-Coded Dots
        foreach (var pair in coordinateGroups)
        {
            int cx = pair.Key.Item1;
            int cy = pair.Key.Item2;
            int repoIdx = pair.Key.Item3;
            int count = pair.Value;
            
            string color = GetStableColor(repoIdx, uniqueActiveRepos.Count);
            double radius = 3.0 + Math.Min(5.0, count * 0.5);

            svg.AppendLine($"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{radius:F1}\" fill=\"{color}\" class=\"commit-dot\"><title>[{uniqueActiveRepos[repoIdx]}] {count} commit(s) here</title></circle>");
        }

        // Render the Repo Color Legend side panel
        int legendX = paddingLeft + chartWidth + 30;
        int legendYStart = paddingTop + 15;
        
        svg.AppendLine($"  <text x=\"{legendX}\" y=\"{legendYStart - 15}\" class=\"text\" style=\"font-weight:bold; fill:#c9d1d9;\">ACTIVE REPOSITORIES ({uniqueActiveRepos.Count})</text>");
        
        for (int i = 0; i < uniqueActiveRepos.Count; i++)
        {
            int itemY = legendYStart + (i * 18);
            
            // Prevent drawing legend items beyond the chart footer boundary
            if (itemY > paddingTop + chartHeight)
            {
                svg.AppendLine($"  <text x=\"{legendX}\" y=\"{itemY}\" class=\"text\" style=\"font-style:italic;\">+ {uniqueActiveRepos.Count - i} more repos...</text>");
                break;
            }

            string color = GetStableColor(i, uniqueActiveRepos.Count);
            string shortName = uniqueActiveRepos[i];
            
            // Truncate long repo owner paths gracefully if they overflow the side panel
            if (shortName.Length > 35) shortName = shortName.Substring(0, 32) + "...";

            svg.AppendLine($"  <circle cx=\"{legendX + 5}\" cy=\"{itemY - 4}\" r=\"5\" fill=\"{color}\" stroke=\"#0d1117\" stroke-width=\"0.5\" />");
            svg.AppendLine($"  <text x=\"{legendX + 18}\" y=\"{itemY}\" class=\"legend-text biographical\">{shortName}</text>");
        }

        svg.AppendLine("</svg>");

        File.WriteAllText(filename, svg.ToString());
        Console.WriteLine($"[Success] Beautiful 4-week color-coded vector graph exported to: {Path.GetFullPath(filename)}");
    }

    // Generates high-contrast, beautiful HSL colors mapped linearly to the repository count
    static string GetStableColor(int index, int totalCount)
    {
        if (totalCount <= 0) totalCount = 1;
        // Distribute hues cleanly across the 360-degree palette circle
        double hue = (index * (360.0 / totalCount)) % 360;
        // High saturation and bright visibility suited specifically for dark modes
        return $"hsl({hue:F1}, 85%, 60%)";
    }

    static string RunGhCommand(string arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = "gh";
        process.StartInfo.Arguments = arguments;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return process.ExitCode == 0 ? output : string.Empty;
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