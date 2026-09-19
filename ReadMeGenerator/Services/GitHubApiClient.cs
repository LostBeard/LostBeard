using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ReadMeGenerator.Services;

public sealed class GitHubApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubApiClient> _logger;

    public GitHubApiClient(HttpClient http, ILogger<GitHubApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public static void Configure(HttpClient http, string token)
    {
        http.BaseAddress = new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ReadMeGenerator", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public Task<string> GetStringAsync(string relativeUrl, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, relativeUrl, body: null, allowNotFound: false, ct)!;

    /// <summary>Returns null on 404.</summary>
    public Task<string?> TryGetStringAsync(string relativeUrl, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, relativeUrl, body: null, allowNotFound: true, ct);

    public Task<string> PostJsonAsync(string relativeUrl, string jsonBody, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, relativeUrl, jsonBody, allowNotFound: false, ct)!;

    private async Task<string?> SendAsync(
        HttpMethod method,
        string url,
        string? body,
        bool allowNotFound,
        CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);

            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return null;

            var status = response.StatusCode;
            var retryable = status is HttpStatusCode.Forbidden
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!retryable || attempt == maxAttempts)
            {
                throw new HttpRequestException(
                    $"GitHub API {method} {url} failed: {(int)status} {status}. {Truncate(responseBody, 500)}");
            }

            var delay = GetRetryDelay(response, attempt);
            _logger.LogWarning(
                "GitHub API {Status} on {Url}; retry {Attempt}/{Max} after {Delay}ms",
                (int)status, url, attempt, maxAttempts, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, ct);
        }

        throw new InvalidOperationException("Unreachable");
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.TryGetValues("Retry-After", out var values)
            && int.TryParse(values.FirstOrDefault(), out var seconds))
            return TimeSpan.FromSeconds(seconds);

        var baseMs = Math.Min(30_000, 500 * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 250));
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
