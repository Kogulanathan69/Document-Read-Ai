using GazetteAI.Application.Documents.Models;

namespace GazetteAI.Application.Documents.Interfaces;

public interface ITextChunker
{
    IReadOnlyList<TextChunk> CreateChunks(
        IReadOnlyList<ExtractedPage> pages,
        int maximumCharacters = 1500,
        int overlapCharacters = 200
    );
}