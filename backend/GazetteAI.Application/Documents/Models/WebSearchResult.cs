namespace GazetteAI.Application.Documents.Models;

public sealed record WebSearchResult(
    string Title,
    string Url,
    string Content,
    double Score);
