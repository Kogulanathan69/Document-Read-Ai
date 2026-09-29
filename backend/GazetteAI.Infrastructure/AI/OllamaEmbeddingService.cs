using System.Net.Http.Json;
using GazetteAI.Application.Documents.Interfaces;
using Microsoft.Extensions.Options;

namespace GazetteAI.Infrastructure.AI;

public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;

    public OllamaEmbeddingService(
        HttpClient httpClient,
        IOptions<OllamaOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress =
            new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    public async Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException(
                "Text cannot be empty.",
                nameof(text));
        }

        var request = new
        {
            model = _options.EmbeddingModel,
            input = text
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "api/embed",
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(
                cancellationToken);

            throw new InvalidOperationException(
                $"Ollama embedding request failed: " +
                $"{response.StatusCode}. {error}");
        }

        var result =
            await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(
                cancellationToken: cancellationToken);

        var embedding = result?.Embeddings?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "Ollama did not return an embedding.");

        if (embedding.Length != _options.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Expected {_options.EmbeddingDimensions} dimensions, " +
                $"but Ollama returned {embedding.Length}.");
        }

        return embedding;
    }

    private sealed class OllamaEmbedResponse
    {
        public float[][] Embeddings { get; set; } = [];
    }
}