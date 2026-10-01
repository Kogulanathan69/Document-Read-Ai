using GazetteAI.Application.Documents.BackgroundProcessing;
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
    private readonly IDocumentProcessingQueue _processingQueue;
    private readonly AppDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;

    public DocumentsController(
        IPdfTextExtractor pdfTextExtractor,
        IOcrTextExtractor ocrTextExtractor,
        ITextChunker textChunker,
        IEmbeddingService embeddingService,
        IDocumentProcessingQueue processingQueue,
        AppDbContext dbContext,
        IWebHostEnvironment environment)
    {
        _pdfTextExtractor = pdfTextExtractor;
        _ocrTextExtractor = ocrTextExtractor;
        _textChunker = textChunker;
        _embeddingService = embeddingService;
        _processingQueue = processingQueue;
        _dbContext = dbContext;
        _environment = environment;
    }

    /*
     * Get documents belonging to the current user.
     */

    [HttpGet]
    public async Task<IActionResult> GetDocuments(
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return InvalidToken();
        }

        var documents = await _dbContext.Documents
            .AsNoTracking()
            .Where(document =>
                document.UserId == userId)
            .OrderByDescending(document =>
                document.UploadedAt)
            .Select(document => new
            {
                documentId = document.Id,
                document.FileName,
                document.ContentType,
                document.FileSize,

                totalPages =
                    document.TotalPages ?? 0,

                totalChunks =
                    document.Chunks.Count,

                document.Status,
                document.UploadedAt,

                conversationCount =
                    document.ChatMessages
                        .Select(message =>
                            message.ConversationId)
                        .Distinct()
                        .Count()
            })
            .ToListAsync(cancellationToken);

        return Ok(documents);
    }

    /*
     * Get conversations for a document.
     */

    [HttpGet("{documentId:guid}/conversations")]
    public async Task<IActionResult> GetConversations(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return InvalidToken();
        }

        var ownsDocument =
            await _dbContext.Documents
                .AsNoTracking()
                .AnyAsync(
                    document =>
                        document.Id == documentId &&
                        document.UserId == userId,
                    cancellationToken);

        if (!ownsDocument)
        {
            return DocumentNotFound();
        }

        var messages =
            await _dbContext.ChatMessages
                .AsNoTracking()
                .Where(message =>
                    message.DocumentId == documentId &&
                    message.UserId == userId)
                .OrderBy(message =>
                    message.CreatedAt)
                .Select(message => new
                {
                    message.ConversationId,
                    message.Role,
                    message.Content,
                    message.CreatedAt
                })
                .ToListAsync(cancellationToken);

        var conversations =
            messages
                .GroupBy(message =>
                    message.ConversationId)
                .Select(group => new
                {
                    conversationId =
                        group.Key,

                    title =
                        group
                            .FirstOrDefault(message =>
                                message.Role.Equals(
                                    "User",
                                    StringComparison
                                        .OrdinalIgnoreCase))
                            ?.Content
                        ?? "Document conversation",

                    messageCount =
                        group.Count(),

                    updatedAt =
                        group.Max(message =>
                            message.CreatedAt)
                })
                .OrderByDescending(item =>
                    item.updatedAt)
                .ToList();

        return Ok(conversations);
    }

    /*
     * Get messages for a conversation.
     */

    [HttpGet(
        "{documentId:guid}/conversations/" +
        "{conversationId:guid}/messages")]
    public async Task<IActionResult>
        GetConversationMessages(
            Guid documentId,
            Guid conversationId,
            CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return InvalidToken();
        }

        var ownsDocument =
            await _dbContext.Documents
                .AsNoTracking()
                .AnyAsync(
                    document =>
                        document.Id == documentId &&
                        document.UserId == userId,
                    cancellationToken);

        if (!ownsDocument)
        {
            return DocumentNotFound();
        }

        var messages =
            await _dbContext.ChatMessages
                .AsNoTracking()
                .Where(message =>
                    message.DocumentId == documentId &&
                    message.ConversationId ==
                        conversationId &&
                    message.UserId == userId)
                .OrderBy(message =>
                    message.CreatedAt)
                .Select(message => new
                {
                    messageId = message.Id,
                    message.Role,
                    message.Content,
                    message.CreatedAt
                })
                .ToListAsync(cancellationToken);

        return Ok(new
        {
            documentId,
            conversationId,
            messages
        });
    }

    /*
     * Delete a document.
     *
     * Database data is deleted first.
     * The saved PDF file is then removed
     * from local storage when possible.
     */

    [HttpDelete("{documentId:guid}")]
    public async Task<IActionResult> DeleteDocument(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return InvalidToken();
        }

        var document =
            await _dbContext.Documents
                .FirstOrDefaultAsync(
                    item =>
                        item.Id == documentId &&
                        item.UserId == userId,
                    cancellationToken);

        if (document is null)
        {
            return DocumentNotFound();
        }

        var storagePath =
            document.StoragePath;

        await using var transaction =
            await _dbContext.Database
                .BeginTransactionAsync(
                    cancellationToken);

        await _dbContext.ChatMessages
            .Where(message =>
                message.DocumentId == documentId &&
                message.UserId == userId)
            .ExecuteDeleteAsync(
                cancellationToken);

        _dbContext.Documents.Remove(document);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await transaction.CommitAsync(
            cancellationToken);

        /*
         * Remove physical PDF after the database
         * transaction has successfully completed.
         */

        if (!string.IsNullOrWhiteSpace(storagePath))
        {
            try
            {
                if (System.IO.File.Exists(storagePath))
                {
                    System.IO.File.Delete(storagePath);
                }
            }
            catch
            {
                /*
                 * File cleanup failure should not turn
                 * a successful database deletion into
                 * an API failure.
                 *
                 * Logging can be added later.
                 */
            }
        }

        return Ok(new
        {
            message =
                "Document deleted successfully.",

            documentId
        });
    }

    /*
     * Test document text extraction.
     *
     * This endpoint intentionally remains
     * synchronous because it is a test endpoint.
     */

    [HttpPost("extract")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> ExtractText(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var validationError =
            ValidatePdf(file);

        if (validationError is not null)
        {
            return BadRequest(
                validationError);
        }

        var pages =
            await ExtractPagesAsync(
                file,
                cancellationToken);

        var chunks =
            _textChunker.CreateChunks(
                pages);

        if (chunks.Count == 0)
        {
            return BadRequest(
                "No readable text was found in the PDF.");
        }

        return Ok(new
        {
            fileName =
                file.FileName,

            totalPages =
                pages.Count,

            totalChunks =
                chunks.Count,

            pages,
            chunks
        });
    }

    /*
     * Test OCR directly.
     */

    [HttpPost("test-ocr")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> TestOcr(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var validationError =
            ValidatePdf(file);

        if (validationError is not null)
        {
            return BadRequest(
                validationError);
        }

        await using var pdfStream =
            file.OpenReadStream();

        var pages =
            await _ocrTextExtractor
                .ExtractAsync(
                    pdfStream,
                    cancellationToken);

        return Ok(new
        {
            fileName =
                file.FileName,

            extractionMethod =
                "Tesseract OCR",

            languages =
                "tam+eng",

            totalPages =
                pages.Count,

            pages
        });
    }

    /*
     * Test embedding generation.
     */

    [HttpPost("test-embedding")]
    public async Task<IActionResult> TestEmbedding(
        [FromBody] EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(
                request.Text))
        {
            return BadRequest(
                "Text is required.");
        }

        var normalizedText =
            request.Text.Trim();

        var embedding =
            await _embeddingService
                .GenerateEmbeddingAsync(
                    $"search_query: {normalizedText}",
                    cancellationToken);

        return Ok(new
        {
            dimensions =
                embedding.Length,

            preview =
                embedding.Take(5)
        });
    }

    /*
     * Upload a PDF.
     *
     * IMPORTANT:
     * Heavy PDF/OCR/chunk/embedding work is NOT
     * performed inside this HTTP request anymore.
     *
     * Flow:
     *
     * 1. Validate request.
     * 2. Generate DocumentId.
     * 3. Save PDF to durable local storage.
     * 4. Create database record with Queued status.
     * 5. Queue background-processing job.
     * 6. Return HTTP 202 Accepted immediately.
     */

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadAndIndex(
        [FromForm] IFormFile file,
        CancellationToken cancellationToken)
    {
        var validationError =
            ValidatePdf(file);

        if (validationError is not null)
        {
            return BadRequest(
                validationError);
        }

        if (!TryGetCurrentUserId(
                out var userId))
        {
            return InvalidToken();
        }

        var userExists =
            await _dbContext.Users
                .AsNoTracking()
                .AnyAsync(
                    user =>
                        user.Id == userId,
                    cancellationToken);

        if (!userExists)
        {
            return NotFound(
                "User was not found.");
        }

        /*
         * Generate the document ID before
         * saving the physical PDF.
         */

        var documentId =
            Guid.NewGuid();

        /*
         * Storage structure:
         *
         * GazetteAI.Api/
         *   Storage/
         *     Documents/
         *       {userId}/
         *         {documentId}.pdf
         *
         * We do NOT use the uploaded filename
         * as the physical storage filename.
         */

        var storageDirectory =
            Path.Combine(
                _environment.ContentRootPath,
                "Storage",
                "Documents",
                userId.ToString());

        Directory.CreateDirectory(
            storageDirectory);

        var storagePath =
            Path.Combine(
                storageDirectory,
                $"{documentId}.pdf");

        /*
         * Save the uploaded PDF before queueing.
         *
         * IFormFile must NOT be placed directly
         * into the background queue because its
         * request stream belongs to this HTTP request.
         */

        try
        {
            await using var destinationStream =
                new FileStream(
                    storagePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);

            await file.CopyToAsync(
                destinationStream,
                cancellationToken);
        }
        catch
        {
            /*
             * Remove an incomplete file if writing
             * was interrupted or failed.
             */

            TryDeleteStoredFile(
                storagePath);

            throw;
        }

        var document =
            new DocumentEntity
            {
                Id =
                    documentId,

                UserId =
                    userId,

                FileName =
                    Path.GetFileName(
                        file.FileName),

                StoragePath =
                    storagePath,

                ContentType =
                    string.IsNullOrWhiteSpace(
                        file.ContentType)
                        ? "application/pdf"
                        : file.ContentType,

                FileSize =
                    file.Length,

                /*
                 * These values are unknown until
                 * the worker processes the PDF.
                 */

                TotalPages =
                    null,

                Status =
                    "Queued",

                UploadedAt =
                    DateTime.UtcNow
            };

        _dbContext.Documents.Add(
            document);

        /*
         * Save the database record before putting
         * the job into the in-memory queue.
         */

        try
        {
            await _dbContext.SaveChangesAsync(
                cancellationToken);
        }
        catch
        {
            TryDeleteStoredFile(
                storagePath);

            throw;
        }

        var job =
            new DocumentProcessingJob(
                document.Id,
                userId,
                storagePath);

        try
        {
            await _processingQueue.QueueAsync(
                job,
                cancellationToken);
        }
        catch
        {
            /*
             * The DB record already exists at this
             * point. If queueing fails, mark it Failed
             * instead of leaving it permanently Queued.
             */

            document.Status =
                "Failed";

            await _dbContext.SaveChangesAsync(
                CancellationToken.None);

            throw;
        }

        /*
         * HTTP 202 means:
         *
         * "The request was accepted, but processing
         * is continuing asynchronously."
         */

        return Accepted(new
        {
            message =
                "PDF uploaded successfully and queued for processing.",

            documentId =
                document.Id,

            fileName =
                document.FileName,

            totalPages =
                0,

            totalChunks =
                0,

            status =
                document.Status,

            uploadedAt =
                document.UploadedAt
        });
    }

    /*
     * Shared extraction logic used by the existing
     * /extract test endpoint.
     *
     * Production upload processing now happens
     * inside DocumentProcessingService.
     */

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

        var pdfBytes =
            memoryStream.ToArray();

        IReadOnlyList<ExtractedPage>
            selectablePages;

        await using (var selectableStream =
            new MemoryStream(pdfBytes))
        {
            selectablePages =
                await _pdfTextExtractor
                    .ExtractAsync(
                        selectableStream,
                        cancellationToken);
        }

        var requiresOcr =
            selectablePages.Count == 0 ||
            selectablePages.Any(page =>
                string.IsNullOrWhiteSpace(
                    page.Text) ||
                page.Text.Trim().Length < 20);

        if (!requiresOcr)
        {
            return selectablePages;
        }

        IReadOnlyList<ExtractedPage>
            ocrPages;

        await using (var ocrStream =
            new MemoryStream(pdfBytes))
        {
            ocrPages =
                await _ocrTextExtractor
                    .ExtractAsync(
                        ocrStream,
                        cancellationToken);
        }

        if (selectablePages.Count == 0)
        {
            return ocrPages;
        }

        var ocrPagesByNumber =
            ocrPages.ToDictionary(
                page =>
                    page.PageNumber);

        return selectablePages
            .Select(page =>
            {
                var hasSelectableText =
                    !string.IsNullOrWhiteSpace(
                        page.Text) &&
                    page.Text.Trim().Length >= 20;

                if (hasSelectableText)
                {
                    return page;
                }

                return ocrPagesByNumber
                    .TryGetValue(
                        page.PageNumber,
                        out var ocrPage)
                            ? ocrPage
                            : page;
            })
            .ToList();
    }

    /*
     * PDF request validation.
     */

    private static string? ValidatePdf(
        IFormFile? file)
    {
        if (file is null ||
            file.Length == 0)
        {
            return
                "Please upload a PDF file.";
        }

        if (!string.Equals(
                Path.GetExtension(
                    file.FileName),
                ".pdf",
                StringComparison
                    .OrdinalIgnoreCase))
        {
            return
                "Only PDF files are allowed.";
        }

        return null;
    }

    /*
     * Get current authenticated user ID.
     */

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

    /*
     * Standard invalid-token response.
     */

    private IActionResult InvalidToken()
    {
        return Unauthorized(new
        {
            message =
                "The authentication token does not " +
                "contain a valid user ID."
        });
    }

    /*
     * Standard document-not-found response.
     */

    private IActionResult DocumentNotFound()
    {
        return NotFound(new
        {
            message =
                "Document was not found for this user."
        });
    }

    /*
     * Best-effort physical file cleanup.
     */

    private static void TryDeleteStoredFile(
        string storagePath)
    {
        if (string.IsNullOrWhiteSpace(
                storagePath))
        {
            return;
        }

        try
        {
            if (System.IO.File.Exists(
                    storagePath))
            {
                System.IO.File.Delete(
                    storagePath);
            }
        }
        catch
        {
            /*
             * Cleanup failure is intentionally ignored.
             * Structured logging can be added later.
             */
        }
    }
}

public sealed record EmbeddingRequest(
    string Text);