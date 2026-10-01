using GazetteAI.Application.Documents.BackgroundProcessing;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using GazetteAI.Domain.Entities;
using GazetteAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GazetteAI.Infrastructure.Documents;

public sealed class DocumentProcessingService
    : IDocumentProcessingService
{
    private readonly IPdfTextExtractor _pdfTextExtractor;
    private readonly IOcrTextExtractor _ocrTextExtractor;
    private readonly ITextChunker _textChunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly AppDbContext _dbContext;

    public DocumentProcessingService(
        IPdfTextExtractor pdfTextExtractor,
        IOcrTextExtractor ocrTextExtractor,
        ITextChunker textChunker,
        IEmbeddingService embeddingService,
        AppDbContext dbContext)
    {
        _pdfTextExtractor = pdfTextExtractor;
        _ocrTextExtractor = ocrTextExtractor;
        _textChunker = textChunker;
        _embeddingService = embeddingService;
        _dbContext = dbContext;
    }

    public async Task ProcessAsync(
        DocumentProcessingJob job,
        CancellationToken cancellationToken)
    {
        var document =
            await _dbContext.Documents
                .FirstOrDefaultAsync(
                    item =>
                        item.Id == job.DocumentId &&
                        item.UserId == job.UserId,
                    cancellationToken);

        if (document is null)
        {
            return;
        }

        document.Status = "Processing";

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        try
        {
            if (!File.Exists(job.StoragePath))
            {
                throw new FileNotFoundException(
                    "The uploaded PDF could not be found.",
                    job.StoragePath);
            }

            var pages =
                await ExtractPagesAsync(
                    job.StoragePath,
                    cancellationToken);

            var chunks =
                _textChunker.CreateChunks(pages);

            if (chunks.Count == 0)
            {
                throw new InvalidOperationException(
                    "No readable text was found in the PDF.");
            }

            /*
             * Remove any existing chunks before
             * processing/reprocessing this document.
             */

            await _dbContext.DocumentChunks
                .Where(chunk =>
                    chunk.DocumentId == document.Id &&
                    chunk.UserId == document.UserId)
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var chunk in chunks)
            {
                var documentText =
                    $"search_document: {chunk.Content}";

                var embedding =
                    await _embeddingService
                        .GenerateEmbeddingAsync(
                            documentText,
                            cancellationToken);

                var documentChunk =
                    new DocumentChunk
                    {
                        Id = Guid.NewGuid(),
                        DocumentId = document.Id,
                        UserId = document.UserId,
                        ChunkIndex = chunk.ChunkIndex,
                        PageNumber = chunk.PageNumber,
                        Content = chunk.Content,
                        Embedding = embedding,
                        TokenCount = chunk.TokenCount,
                        CreatedAt = DateTime.UtcNow
                    };

                _dbContext.DocumentChunks.Add(
                    documentChunk);
            }

            document.TotalPages = pages.Count;
            document.Status = "Ready";

            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
        catch
        {
            /*
             * Do not mark application shutdown/cancellation
             * as a document-processing failure.
             */

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            document.Status = "Failed";

            await _dbContext.SaveChangesAsync(
                CancellationToken.None);

            throw;
        }
    }

    private async Task<IReadOnlyList<ExtractedPage>>
        ExtractPagesAsync(
            string storagePath,
            CancellationToken cancellationToken)
    {
        var pdfBytes =
            await File.ReadAllBytesAsync(
                storagePath,
                cancellationToken);

        IReadOnlyList<ExtractedPage> selectablePages;

        await using (var selectableStream =
            new MemoryStream(pdfBytes))
        {
            selectablePages =
                await _pdfTextExtractor.ExtractAsync(
                    selectableStream,
                    cancellationToken);
        }

        var requiresOcr =
            selectablePages.Count == 0 ||
            selectablePages.Any(page =>
                string.IsNullOrWhiteSpace(page.Text) ||
                page.Text.Trim().Length < 20);

        if (!requiresOcr)
        {
            return selectablePages;
        }

        IReadOnlyList<ExtractedPage> ocrPages;

        await using (var ocrStream =
            new MemoryStream(pdfBytes))
        {
            ocrPages =
                await _ocrTextExtractor.ExtractAsync(
                    ocrStream,
                    cancellationToken);
        }

        if (selectablePages.Count == 0)
        {
            return ocrPages;
        }

        var ocrPagesByNumber =
            ocrPages.ToDictionary(
                page => page.PageNumber);

        return selectablePages
            .Select(page =>
            {
                var hasSelectableText =
                    !string.IsNullOrWhiteSpace(page.Text) &&
                    page.Text.Trim().Length >= 20;

                if (hasSelectableText)
                {
                    return page;
                }

                return ocrPagesByNumber.TryGetValue(
                    page.PageNumber,
                    out var ocrPage)
                        ? ocrPage
                        : page;
            })
            .ToList();
    }
}