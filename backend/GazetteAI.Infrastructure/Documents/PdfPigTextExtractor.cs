using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace GazetteAI.Infrastructure.Documents;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public async Task<IReadOnlyList<ExtractedPage>> ExtractAsync(
        Stream pdfStream,
        CancellationToken cancellationToken = default)
    {
        if (!pdfStream.CanRead)
        {
            throw new ArgumentException(
                "PDF stream cannot be read.",
                nameof(pdfStream));
        }

        await using var memoryStream = new MemoryStream();

        await pdfStream.CopyToAsync(
            memoryStream,
            cancellationToken);

        using var document = PdfDocument.Open(
            memoryStream.ToArray());

        var extractedPages =
            new List<ExtractedPage>(document.NumberOfPages);

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = ContentOrderTextExtractor
                .GetText(page)
                .Trim();

            extractedPages.Add(
                new ExtractedPage(page.Number, text));
        }

        return extractedPages;
    }
}