using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.WriteLine("Error: GITHUB_TOKEN environment variable is missing.");
    Environment.Exit(1);
}

var goal = 500.00; // Your monthly goal
using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpawnDev-Funding-Bot", "1.0"));

// GraphQL payload to query active individual tier values
var query = "{ \"query\": \"query { viewer { sponsorshipsAsMaintainer(first: 100) { nodes { tier { monthlyPriceInCents } } } } }\" }";

var response = await client.PostAsync("https://api.github.com/graphql", new StringContent(query));
if (!response.IsSuccessStatusCode)
{
    Console.WriteLine($"Error: GitHub API returned status code {response.StatusCode}");
    Environment.Exit(1);
}

var json = await response.Content.ReadAsStringAsync();

// Parse out the array values natively via Regex
var matches = Regex.Matches(json, @"""monthlyPriceInCents"":\s*(\d+)");
double currentCents = matches.Sum(m => double.Parse(m.Groups[1].Value));
double currentTotal = currentCents / 100.0;

// Calculate metrics based on a 400px maximum bar canvas width
double percent = Math.Min((currentTotal / goal) * 100, 100);
int pixelWidth = (int)(400 * (percent / 100));

// Ensure the asset path exists
if (!File.Exists("progress.svg"))
{
    Console.WriteLine("Error: Source file 'progress.svg' template not found in working root.");
    Environment.Exit(1);
}

// Update the vector template
var template = await File.ReadAllTextAsync("progress.svg");
var output = template
    .Replace("{PERCENT_WIDTH}", pixelWidth.ToString())
    .Replace("{CURRENT}", currentTotal.ToString("N0"))
    .Replace("{GOAL}", goal.ToString("N0"))
    .Replace("{PERCENT}", percent.ToString("N1"));

// Output target destination
Directory.CreateDirectory("assets");
await File.WriteAllTextAsync("assets/progress.svg", output);

Console.WriteLine($"Funding progress calculation verified: ${currentTotal} / ${goal} ({percent:F1}%)");