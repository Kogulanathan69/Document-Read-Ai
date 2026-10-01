using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using GazetteAI.Domain.Entities;
using GazetteAI.Infrastructure.AI;
using GazetteAI.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GazetteAI.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class QuestionsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IEmbeddingService _embeddingService;
    private readonly IChatCompletionService _chatService;
    private readonly IWebSearchService _webSearchService;
    private readonly IDistributedCache _cache;
    private readonly ILogger<QuestionsController> _logger;

    public QuestionsController(
        AppDbContext dbContext,
        IEmbeddingService embeddingService,
        IChatCompletionService chatService,
        IWebSearchService webSearchService,
        IDistributedCache cache,
        ILogger<QuestionsController> logger)
    {
        _dbContext = dbContext;
        _embeddingService = embeddingService;
        _chatService = chatService;
        _webSearchService = webSearchService;
        _cache = cache;
        _logger = logger;
    }

    [HttpPost("ask")]
    public async Task<IActionResult> AskQuestion(
        [FromBody] AskDocumentRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new
            {
                message =
                    "The authentication token does not " +
                    "contain a valid user ID."
            });
        }

        var validationResult =
            ValidateRequest(request);

        if (validationResult is not null)
        {
            return BadRequest(new
            {
                message = validationResult
            });
        }

        var normalizedQuestion =
            request.Question.Trim();

        try
        {
            var document =
                await _dbContext.Documents
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        item =>
                            item.Id ==
                                request.DocumentId &&
                            item.UserId ==
                                userId,
                        cancellationToken);

            if (document is null)
            {
                return NotFound(new
                {
                    message =
                        "Document was not found for this user."
                });
            }

            var conversationId =
                request.ConversationId.HasValue &&
                request.ConversationId.Value != Guid.Empty
                    ? request.ConversationId.Value
                    : Guid.NewGuid();

            /*
             * Latest 12 messages only.
             * This keeps Groq token usage smaller.
             */

            var storedHistory =
                await _dbContext.ChatMessages
                    .AsNoTracking()
                    .Where(message =>
                        message.ConversationId ==
                            conversationId &&
                        message.UserId ==
                            userId &&
                        message.DocumentId ==
                            request.DocumentId)
                    .OrderByDescending(message =>
                        message.CreatedAt)
                    .Take(12)
                    .ToListAsync(cancellationToken);

            /*
             * Database result is newest-to-oldest.
             * Reverse it back to conversation order.
             */

            storedHistory.Reverse();

            var chatHistory = storedHistory
                .Select(message =>
                    new ChatHistoryMessage(
                        message.Role,
                        message.Content))
                .ToList();

            /*
             * Analyze language, writing style,
             * follow-up, clarification, translation,
             * and standalone search question.
             */

            var analysis =
                await _chatService
                    .AnalyzeConversationAsync(
                        normalizedQuestion,
                        chatHistory,
                        cancellationToken);

            var searchQuestion =
                string.IsNullOrWhiteSpace(
                    analysis.SearchQuestion)
                    ? normalizedQuestion
                    : analysis.SearchQuestion.Trim();

            /*
             * Clarification / translation fallback:
             * use the previous user message when the
             * analysis did not create a better
             * standalone search question.
             */

            if ((analysis.IsClarification ||
                 analysis.IsTranslationRequest) &&
                IsSameText(
                    searchQuestion,
                    normalizedQuestion))
            {
                var previousUserMessage =
                    storedHistory
                        .LastOrDefault(message =>
                            message.Role.Equals(
                                "User",
                                StringComparison
                                    .OrdinalIgnoreCase))
                        ?.Content;

                if (!string.IsNullOrWhiteSpace(
                        previousUserMessage))
                {
                    searchQuestion =
                        previousUserMessage.Trim();
                }
            }

            /*
             * Greeting / acknowledgement / permission
             * responses do not always require document
             * retrieval.
             */

            if (!analysis.RequiresDocumentSearch)
            {
                /*
                 * User granted permission to search
                 * outside the uploaded document.
                 */

                if (analysis.ExternalPermissionGranted &&
                    !string.IsNullOrWhiteSpace(
                        analysis.ExternalQuestion))
                {
                    var externalQuestion =
                        analysis.ExternalQuestion.Trim();

                    var webResults =
                        await _webSearchService.SearchAsync(
                            externalQuestion,
                            cancellationToken);

                    var webAnswer =
                        await _chatService
                            .GenerateWebAnswerAsync(
                                externalQuestion,
                                webResults,
                                analysis,
                                cancellationToken);

                    await SaveConversationMessagesAsync(
                        conversationId,
                        userId,
                        request.DocumentId,
                        normalizedQuestion,
                        webAnswer,
                        cancellationToken);

                    var webSources = webResults
                        .Select(item => new
                        {
                            title = item.Title,
                            url = item.Url
                        })
                        .ToList();

                    return Ok(new
                    {
                        conversationId,
                        documentId = document.Id,
                        fileName = document.FileName,
                        question = normalizedQuestion,
                        searchQuestion = externalQuestion,
                        intent = analysis.Intent,

                        requiresDocumentSearch =
                            false,

                        detectedLanguage =
                            analysis.DetectedLanguage,

                        writingStyle =
                            analysis.WritingStyle,

                        responseInstruction =
                            analysis.ResponseInstruction,

                        isFollowUp = true,
                        clarificationRequest = false,
                        translationRequest = false,
                        requiresExternalKnowledge = true,
                        externalPermissionResponse = true,
                        externalPermissionGranted = true,
                        answerSource = "live-web",
                        answer = webAnswer,

                        sources =
                            Array.Empty<object>(),

                        webSources
                    });
                }

                var directAnswer =
                    string.IsNullOrWhiteSpace(
                        analysis.DirectResponse)
                        ? "Okay! What else would you like " +
                          "to know about this document?"
                        : analysis.DirectResponse.Trim();

                await SaveConversationMessagesAsync(
                    conversationId,
                    userId,
                    request.DocumentId,
                    normalizedQuestion,
                    directAnswer,
                    cancellationToken);

                return Ok(new
                {
                    conversationId,

                    documentId =
                        document.Id,

                    fileName =
                        document.FileName,

                    question =
                        normalizedQuestion,

                    searchQuestion =
                        string.Empty,

                    intent =
                        analysis.Intent,

                    requiresDocumentSearch =
                        false,

                    detectedLanguage =
                        analysis.DetectedLanguage,

                    writingStyle =
                        analysis.WritingStyle,

                    responseInstruction =
                        analysis.ResponseInstruction,

                    isFollowUp =
                        analysis.IsFollowUp,

                    clarificationRequest =
                        analysis.IsClarification,

                    translationRequest =
                        analysis.IsTranslationRequest,

                    requiresExternalKnowledge =
                        analysis.RequiresExternalKnowledge,

                    externalPermissionResponse =
                        analysis.IsExternalPermissionResponse,

                    externalPermissionGranted =
                        analysis.ExternalPermissionGranted,

                    answerSource =
                        analysis.IsTranslationRequest ||
                        analysis.IsClarification
                            ? "conversation"
                            : analysis.ExternalPermissionGranted
                                ? "general-knowledge"
                                : "assistant",

                    answer =
                        directAnswer,

                    sources =
                        Array.Empty<object>()
                });
            }

            /*
             * Load searchable chunks belonging only
             * to this authenticated user's document.
             */

            var chunks =
                await _dbContext.DocumentChunks
                    .AsNoTracking()
                    .Where(chunk =>
                        chunk.DocumentId ==
                            request.DocumentId &&
                        chunk.UserId ==
                            userId)
                    .ToListAsync(cancellationToken);

            if (chunks.Count == 0)
            {
                return BadRequest(new
                {
                    message =
                        "This document does not contain " +
                        "searchable text."
                });
            }

            /*
             * Redis embedding cache
             *
             * Flow:
             *
             * Search question
             *      |
             *      v
             * Redis GET
             *   /     \
             * HIT     MISS
             *  |        |
             * reuse    Ollama
             *           |
             *        Redis SET
             *
             * The embedding is based only on the
             * normalized search text and embedding
             * model input format. It does not contain
             * document contents.
             */

            var questionEmbedding =
                await GetOrCreateQuestionEmbeddingAsync(
                    searchQuestion,
                    cancellationToken);

            /*
             * Compare question embedding against
             * stored document chunk embeddings.
             */

            var relevantChunks = chunks
                .Select(chunk => new
                {
                    Chunk = chunk,

                    Score =
                        CalculateCosineSimilarity(
                            questionEmbedding,
                            chunk.Embedding)
                })
                .OrderByDescending(item =>
                    item.Score)
                .Take(4)
                .ToList();

            var contextChunks = relevantChunks
                .Select(item =>
                    $"[Page " +
                    $"{item.Chunk.PageNumber}]\n" +
                    item.Chunk.Content)
                .ToList();

            /*
             * Generate final answer using:
             *
             * - standalone search question
             * - relevant document chunks
             * - previous chat history
             * - multilingual conversation analysis
             */

            var answer =
                await _chatService
                    .GenerateAnswerAsync(
                        searchQuestion,
                        contextChunks,
                        chatHistory,
                        analysis,
                        cancellationToken);

            if (string.IsNullOrWhiteSpace(answer))
            {
                return StatusCode(
                    StatusCodes
                        .Status503ServiceUnavailable,
                    new
                    {
                        message =
                            "The AI service did not " +
                            "return an answer. " +
                            "Please try again."
                    });
            }

            /*
             * Document retrieval could not ground
             * an answer.
             *
             * Ask permission before using web /
             * external knowledge.
             */

            if (answer.Equals(
                    GroqChatCompletionService
                        .DocumentAnswerNotFoundMarker,
                    StringComparison.Ordinal))
            {
                var permissionPrompt =
                    BuildExternalPermissionPrompt(
                        analysis.ResponseLanguage);

                await SaveConversationMessagesAsync(
                    conversationId,
                    userId,
                    request.DocumentId,
                    normalizedQuestion,
                    permissionPrompt,
                    cancellationToken);

                return Ok(new
                {
                    conversationId,
                    documentId = document.Id,
                    fileName = document.FileName,
                    question = normalizedQuestion,
                    searchQuestion,
                    intent = "GeneralQuestion",

                    requiresDocumentSearch =
                        false,

                    detectedLanguage =
                        analysis.DetectedLanguage,

                    writingStyle =
                        analysis.WritingStyle,

                    responseInstruction =
                        analysis.ResponseInstruction,

                    isFollowUp =
                        analysis.IsFollowUp,

                    clarificationRequest =
                        analysis.IsClarification,

                    translationRequest =
                        analysis.IsTranslationRequest,

                    requiresExternalKnowledge =
                        true,

                    externalPermissionResponse =
                        false,

                    externalPermissionGranted =
                        false,

                    answerSource =
                        "document-not-found",

                    answer =
                        permissionPrompt,

                    sources =
                        Array.Empty<object>()
                });
            }

            await SaveConversationMessagesAsync(
                conversationId,
                userId,
                request.DocumentId,
                normalizedQuestion,
                answer.Trim(),
                cancellationToken);

            /*
             * If multiple chunks come from the same
             * page, return that page only once in the
             * frontend source list.
             */

            var sources = relevantChunks
                .GroupBy(item =>
                    item.Chunk.PageNumber)
                .Select(group =>
                {
                    var bestMatch = group
                        .OrderByDescending(item =>
                            item.Score)
                        .First();

                    return new
                    {
                        pageNumber =
                            bestMatch
                                .Chunk
                                .PageNumber,

                        chunkIndex =
                            bestMatch
                                .Chunk
                                .ChunkIndex,

                        similarityScore =
                            Math.Round(
                                bestMatch.Score,
                                4)
                    };
                })
                .OrderBy(source =>
                    source.pageNumber)
                .ToList();

            return Ok(new
            {
                conversationId,

                documentId =
                    document.Id,

                fileName =
                    document.FileName,

                question =
                    normalizedQuestion,

                searchQuestion,

                intent =
                    analysis.Intent,

                requiresDocumentSearch =
                    analysis.RequiresDocumentSearch,

                detectedLanguage =
                    analysis.DetectedLanguage,

                writingStyle =
                    analysis.WritingStyle,

                responseInstruction =
                    analysis.ResponseInstruction,

                isFollowUp =
                    analysis.IsFollowUp,

                clarificationRequest =
                    analysis.IsClarification,

                translationRequest =
                    analysis.IsTranslationRequest,

                requiresExternalKnowledge =
                    analysis.RequiresExternalKnowledge,

                externalPermissionResponse =
                    analysis.IsExternalPermissionResponse,

                externalPermissionGranted =
                    analysis.ExternalPermissionGranted,

                answerSource =
                    "document",

                answer =
                    answer.Trim(),

                sources
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(
                StatusCodes.Status408RequestTimeout,
                new
                {
                    message =
                        "The request was cancelled " +
                        "or took too long."
                });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(
                exception,
                "External AI service connection failed.");

            return StatusCode(
                StatusCodes
                    .Status503ServiceUnavailable,
                new
                {
                    message =
                        "The AI service is temporarily " +
                        "unavailable. Please try again."
                });
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogError(
                exception,
                "AI service operation failed.");

            return StatusCode(
                StatusCodes
                    .Status503ServiceUnavailable,
                new
                {
                    message =
                        "The AI service could not generate " +
                        "an answer. Please try again."
                });
        }
        catch (DbUpdateException exception)
        {
            _logger.LogError(
                exception,
                "Chat message database save failed.");

            return StatusCode(
                StatusCodes
                    .Status500InternalServerError,
                new
                {
                    message =
                        "The answer was generated, but " +
                        "the conversation could not be saved."
                });
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Question answering failed.");

            return StatusCode(
                StatusCodes
                    .Status500InternalServerError,
                new
                {
                    message =
                        "Could not generate an answer. " +
                        "Please try again."
                });
        }
    }

    /*
     * Get question embedding from Redis when available.
     *
     * Cache MISS:
     *     Ollama generates the embedding and it is
     *     stored in Redis.
     *
     * Cache HIT:
     *     Ollama call is skipped.
     */

    private async Task<float[]>
        GetOrCreateQuestionEmbeddingAsync(
            string searchQuestion,
            CancellationToken cancellationToken)
    {
        var normalizedSearchQuestion =
            searchQuestion.Trim();

        var cacheKey =
            BuildEmbeddingCacheKey(
                normalizedSearchQuestion);

        try
        {
            var cachedJson =
                await _cache.GetStringAsync(
                    cacheKey,
                    cancellationToken);

            if (!string.IsNullOrWhiteSpace(
                    cachedJson))
            {
                var cachedEmbedding =
                    JsonSerializer.Deserialize<float[]>(
                        cachedJson);

                if (cachedEmbedding is
                    { Length: > 0 })
                {
                    _logger.LogInformation(
                        "Redis embedding cache HIT. Key: {CacheKey}",
                        cacheKey);

                    return cachedEmbedding;
                }
            }
        }
        catch (Exception exception)
            when (exception is not
                  OperationCanceledException)
        {
            /*
             * Redis should improve performance,
             * not make document Q&A unavailable.
             *
             * If Redis GET fails, continue using
             * Ollama normally.
             */

            _logger.LogWarning(
                exception,
                "Redis embedding cache GET failed. " +
                "Falling back to Ollama.");
        }

        _logger.LogInformation(
            "Redis embedding cache MISS. Key: {CacheKey}",
            cacheKey);

        var embedding =
            await _embeddingService
                .GenerateEmbeddingAsync(
                    $"search_query: " +
                    normalizedSearchQuestion,
                    cancellationToken);

        /*
         * Cache only valid embeddings.
         */

        if (embedding.Length > 0)
        {
            try
            {
                var serializedEmbedding =
                    JsonSerializer.Serialize(
                        embedding);

                await _cache.SetStringAsync(
                    cacheKey,
                    serializedEmbedding,
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow =
                            TimeSpan.FromHours(24)
                    },
                    cancellationToken);

                _logger.LogInformation(
                    "Question embedding stored in Redis. " +
                    "Key: {CacheKey}",
                    cacheKey);
            }
            catch (Exception exception)
                when (exception is not
                      OperationCanceledException)
            {
                /*
                 * Redis SET failure must not fail
                 * the user's question.
                 */

                _logger.LogWarning(
                    exception,
                    "Redis embedding cache SET failed. " +
                    "Continuing without cache.");
            }
        }

        return embedding;
    }

    /*
     * Create a deterministic Redis key without putting
     * the user's full question text into the key.
     *
     * SHA-256 avoids long / sensitive Redis keys.
     *
     * v1 lets us invalidate old embeddings later if
     * the embedding model or input format changes.
     */

    private static string BuildEmbeddingCacheKey(
        string searchQuestion)
    {
        var normalized =
            searchQuestion
                .Trim()
                .ToLowerInvariant();

        var bytes =
            Encoding.UTF8.GetBytes(
                normalized);

        var hash =
            SHA256.HashData(bytes);

        var hashText =
            Convert.ToHexString(hash)
                .ToLowerInvariant();

        return
            $"embedding:v1:{hashText}";
    }

    private async Task SaveConversationMessagesAsync(
        Guid conversationId,
        Guid userId,
        Guid documentId,
        string userContent,
        string assistantContent,
        CancellationToken cancellationToken)
    {
        var currentTime =
            DateTime.UtcNow;

        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            DocumentId = documentId,
            Role = "User",
            Content = userContent.Trim(),
            CreatedAt = currentTime
        };

        var assistantMessage = new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            DocumentId = documentId,
            Role = "Assistant",
            Content = assistantContent.Trim(),
            CreatedAt =
                currentTime.AddMilliseconds(1)
        };

        _dbContext.ChatMessages.AddRange(
            userMessage,
            assistantMessage);

        await _dbContext.SaveChangesAsync(
            cancellationToken);
    }

    private static string? ValidateRequest(
        AskDocumentRequest request)
    {
        if (request.DocumentId == Guid.Empty)
        {
            return "Valid documentId is required.";
        }

        if (string.IsNullOrWhiteSpace(
                request.Question))
        {
            return "Question is required.";
        }

        if (request.Question.Length > 4000)
        {
            return
                "Question cannot exceed 4000 characters.";
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

    private static string BuildExternalPermissionPrompt(
        string? language)
    {
        return language?
            .Trim()
            .ToLowerInvariant() switch
        {
            "tamil" =>
                "இந்தக் கேள்விக்கான பதில் பதிவேற்றிய ஆவணத்தில் இல்லை. " +
                "நீங்கள் விரும்பினால், இணையத்தில் தேடி சமீபத்திய " +
                "தகவல்களுடன் பதில் சொல்லவா?",

            "tanglish" =>
                "Indha question-oda answer uploaded document-la illa. " +
                "Neenga virumbina, web-la search panni latest " +
                "information-oda answer sollava?",

            "sinhala" =>
                "මෙම ප්‍රශ්නයට පිළිතුර උඩුගත කළ ලේඛනයේ නැහැ. " +
                "ඔබ කැමති නම්, වෙබ් අඩවියේ සොයා නවතම " +
                "තොරතුරු සමඟ පිළිතුරු දෙන්නද?",

            "singlish" =>
                "Me question eke answer uploaded document eke naha. " +
                "Oya kamathi nam web eke search karala latest " +
                "information ekka answer karannada?",

            "french" =>
                "La réponse à cette question ne figure pas dans le " +
                "document. Voulez-vous que je recherche sur le Web " +
                "et réponde avec des informations récentes ?",

            "hindi" =>
                "इस प्रश्न का उत्तर अपलोड किए गए दस्तावेज़ में नहीं है। " +
                "क्या मैं वेब पर खोजकर नवीनतम जानकारी के साथ उत्तर दूँ?",

            _ =>
                "The answer to this question is not available in the " +
                "uploaded document. Would you like me to search the web " +
                "and answer using current information?"
        };
    }

    private static bool IsSameText(
        string first,
        string second)
    {
        return string.Equals(
            first.Trim(),
            second.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static double
        CalculateCosineSimilarity(
            float[] first,
            float[] second)
    {
        if (first.Length == 0 ||
            second.Length == 0 ||
            first.Length != second.Length)
        {
            return 0;
        }

        double dotProduct = 0;
        double firstMagnitude = 0;
        double secondMagnitude = 0;

        for (var index = 0;
             index < first.Length;
             index++)
        {
            dotProduct +=
                first[index] *
                second[index];

            firstMagnitude +=
                first[index] *
                first[index];

            secondMagnitude +=
                second[index] *
                second[index];
        }

        if (firstMagnitude == 0 ||
            secondMagnitude == 0)
        {
            return 0;
        }

        return dotProduct /
               (
                   Math.Sqrt(firstMagnitude) *
                   Math.Sqrt(secondMagnitude)
               );
    }
}

public sealed record AskDocumentRequest(
    Guid DocumentId,
    string Question,
    Guid? ConversationId);