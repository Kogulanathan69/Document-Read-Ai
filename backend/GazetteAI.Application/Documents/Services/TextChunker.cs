using System.Text.RegularExpressions;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;

namespace GazetteAI.Application.Documents.Services;

public sealed class TextChunker : ITextChunker
{
    public IReadOnlyList<TextChunk> CreateChunks(
        IReadOnlyList<ExtractedPage> pages,
        int maximumCharacters = 1500,
        int overlapCharacters = 200)
    {
        if (maximumCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCharacters));
        }

        if (overlapCharacters < 0 ||
            overlapCharacters >= maximumCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapCharacters));
        }

        var chunks = new List<TextChunk>();
        var chunkIndex = 0;

        foreach (var page in pages)
        {
            var normalizedText = Regex.Replace(
                page.Text,
                @"\s+",
                " ").Trim();

            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                continue;
            }

            var startPosition = 0;

            while (startPosition < normalizedText.Length)
            {
                var remainingCharacters =
                    normalizedText.Length - startPosition;

                var chunkLength = Math.Min(
                    maximumCharacters,
                    remainingCharacters);

                var endPosition =
                    startPosition + chunkLength;

                if (endPosition < normalizedText.Length)
                {
                    var lastSpacePosition =
                        normalizedText.LastIndexOf(
                            ' ',
                            endPosition - 1,
                            chunkLength);

                    if (lastSpacePosition > startPosition)
                    {
                        endPosition = lastSpacePosition;
                    }
                }

                var content = normalizedText[
                    startPosition..endPosition].Trim();

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var approximateTokenCount =
                        (int)Math.Ceiling(content.Length / 4.0);

                    chunks.Add(new TextChunk(
                        chunkIndex,
                        page.PageNumber,
                        content,
                        approximateTokenCount));

                    chunkIndex++;
                }

                if (endPosition >= normalizedText.Length)
                {
                    break;
                }

                startPosition = Math.Max(
                    endPosition - overlapCharacters,
                    startPosition + 1);
            }
        }

        return chunks;
    }
}