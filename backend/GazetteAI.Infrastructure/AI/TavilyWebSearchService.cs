using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using Microsoft.Extensions.Options;

namespace GazetteAI.Infrastructure.AI;

public sealed class TavilyWebSearchService : IWebSearchService
{
    private const int MaximumContentLength = 4_000;

    private readonly HttpClient _httpClient;
    private readonly TavilyOptions _options;

    public TavilyWebSearchService(
        HttpClient httpClient,
        IOptions<TavilyOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress = new Uri(
            _options.BaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "Tavily API key is missing. Configure Tavily:ApiKey using .NET user-secrets.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "search");

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _options.ApiKey.Trim());

        var currentDate = DateTime.UtcNow
            .ToString("yyyy-MM-dd");

        var currentQuery =
            $"{query.Trim()} (Current date: {currentDate}. " +
            "Use the latest reliable information and prefer " +
            "official authoritative sources.)";

        request.Content = JsonContent.Create(new TavilySearchRequest
        {
            Query = currentQuery,
            SearchDepth = "advanced",
            Topic = "general",
            MaxResults = Math.Clamp(_options.MaxResults, 1, 8),
            IncludeAnswer = false,
            IncludeRawContent = false
        });

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(
                cancellationToken);

            throw new InvalidOperationException(
                $"Tavily search failed: {(int)response.StatusCode} {response.StatusCode}. {error}");
        }

        var result = await response.Content
            .ReadFromJsonAsync<TavilySearchResponse>(
                cancellationToken: cancellationToken);

        return result?.Results
            .Where(item =>
                IsSafeWebUrl(item.Url) &&
                !string.IsNullOrWhiteSpace(item.Content))
            .Take(Math.Clamp(_options.MaxResults, 1, 8))
            .Select(item => new WebSearchResult(
                string.IsNullOrWhiteSpace(item.Title)
                    ? "Web source"
                    : item.Title.Trim(),
                item.Url.Trim(),
                Truncate(item.Content.Trim()),
                item.Score))
            .ToList() ?? [];
    }

    private static bool IsSafeWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp ||
         uri.Scheme == Uri.UriSchemeHttps);

    private static string Truncate(string value) =>
        value.Length <= MaximumContentLength
            ? value
            : value[..MaximumContentLength];

    private sealed class TavilySearchRequest
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = string.Empty;

        [JsonPropertyName("search_depth")]
        public string SearchDepth { get; set; } = "basic";

        [JsonPropertyName("topic")]
        public string Topic { get; set; } = "general";

        [JsonPropertyName("max_results")]
        public int MaxResults { get; set; }

        [JsonPropertyName("include_answer")]
        public bool IncludeAnswer { get; set; }

        [JsonPropertyName("include_raw_content")]
        public bool IncludeRawContent { get; set; }
    }

    private sealed class TavilySearchResponse
    {
        [JsonPropertyName("results")]
        public List<TavilySearchItem> Results { get; set; } = [];
    }

    private sealed class TavilySearchItem
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("score")]
        public double Score { get; set; }
    }
}
