using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using GazetteAI.Domain.Entities;
using GazetteAI.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

using DocumentEntity =
    GazetteAI.Domain.Entities.Document;

namespace GazetteAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IPdfTextExtractor _pdfTextExtractor;
    private readonly IOcrTextExtractor _ocrTextExtractor;
    private readonly ITextChunker _textChunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly AppDbContext _dbContext;

    public DocumentsController(
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

    [HttpPost("extract")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> ExtractText(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var validationError = ValidatePdf(file);

        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        var pages = await ExtractPagesAsync(
            file,
            cancellationToken);

        var chunks = _textChunker.CreateChunks(pages);

        if (chunks.Count == 0)
        {
            return BadRequest(
                "No readable text was found in the PDF.");
        }

        return Ok(new
        {
            fileName = file.FileName,
            totalPages = pages.Count,
            totalChunks = chunks.Count,
            pages,
            chunks
        });
    }

    [HttpPost("test-ocr")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> TestOcr(
    [FromForm] IFormFile file,
    CancellationToken cancellationToken)
    {
        var validationError = ValidatePdf(file);

        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        await using var pdfStream =
            file.OpenReadStream();

        var pages =
            await _ocrTextExtractor.ExtractAsync(
                pdfStream,
                cancellationToken);

        return Ok(new
        {
            fileName = file.FileName,
            extractionMethod = "Tesseract OCR",
            languages = "tam+eng",
            totalPages = pages.Count,
            pages
        });
    }

    [HttpPost("test-embedding")]
    public async Task<IActionResult> TestEmbedding(
        [FromBody] EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return BadRequest("Text is required.");
        }

        var normalizedText = request.Text.Trim();

        var embedding =
            await _embeddingService.GenerateEmbeddingAsync(
                $"search_query: {normalizedText}",
                cancellationToken);

        return Ok(new
        {
            dimensions = embedding.Length,
            preview = embedding.Take(5)
        });
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadAndIndex(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var validationError = ValidatePdf(file);

        if (validationError is not null)
        {
            return BadRequest(validationError);
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "The authentication token does not contain a valid user ID."
            });
        }

        var userExists =
            await _dbContext.Users.AnyAsync(
                user => user.Id == userId,
                cancellationToken);

        if (!userExists)
        {
            return NotFound("User was not found.");
        }

        var pages = await ExtractPagesAsync(
            file,
            cancellationToken);

        var chunks = _textChunker.CreateChunks(pages);

        if (chunks.Count == 0)
        {
            return BadRequest(
                "No readable text was found in the PDF.");
        }

        var document = new DocumentEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FileName = Path.GetFileName(file.FileName),
            StoragePath = string.Empty,

            ContentType =
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/pdf"
                    : file.ContentType,

            FileSize = file.Length,
            TotalPages = pages.Count,
            Status = "Processing",
            UploadedAt = DateTime.UtcNow
        };

        foreach (var chunk in chunks)
        {
            var documentText =
                $"search_document: {chunk.Content}";

            var embedding =
                await _embeddingService.GenerateEmbeddingAsync(
                    documentText,
                    cancellationToken);

            document.Chunks.Add(
                new DocumentChunk
                {
                    Id = Guid.NewGuid(),
                    DocumentId = document.Id,
                    UserId = userId,
                    ChunkIndex = chunk.ChunkIndex,
                    PageNumber = chunk.PageNumber,
                    Content = chunk.Content,
                    Embedding = embedding,
                    TokenCount = chunk.TokenCount,
                    CreatedAt = DateTime.UtcNow
                });
        }

        document.Status = "Ready";

        _dbContext.Documents.Add(document);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return Ok(new
        {
            message =
                "PDF uploaded and indexed successfully.",

            documentId = document.Id,
            fileName = document.FileName,
            totalPages = document.TotalPages,
            totalChunks = document.Chunks.Count,
            embeddingDimensions = 768,
            status = document.Status
        });
    }

    private async Task<IReadOnlyList<ExtractedPage>>
        ExtractPagesAsync(
            IFormFile file,
            CancellationToken cancellationToken)
    {
        await using var uploadedStream =
            file.OpenReadStream();

        await using var memoryStream =
            new MemoryStream();

        await uploadedStream.CopyToAsync(
            memoryStream,
            cancellationToken);

        var pdfBytes = memoryStream.ToArray();

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

    private static string? ValidatePdf(
        IFormFile? file)
    {
        if (file is null ||
            file.Length == 0)
        {
            return "Please upload a PDF file.";
        }

        if (!string.Equals(
                Path.GetExtension(file.FileName),
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            return "Only PDF files are allowed.";
        }

        return null;
    }

    private bool TryGetCurrentUserId(
        out Guid userId)
    {
        var userIdValue =
            User.FindFirstValue(
                ClaimTypes.NameIdentifier);

        return Guid.TryParse(
            userIdValue,
            out userId) &&
            userId != Guid.Empty;
    }
}

public sealed record EmbeddingRequest(
    string Text);
