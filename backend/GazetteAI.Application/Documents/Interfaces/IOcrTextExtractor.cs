using GazetteAI.Application.Documents.Models;

namespace GazetteAI.Application.Documents.Interfaces;

public interface IOcrTextExtractor
{
    Task<IReadOnlyList<ExtractedPage>> ExtractAsync(
        Stream pdfStream,
        CancellationToken cancellationToken = default);
}