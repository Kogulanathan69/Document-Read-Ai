using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GazetteAI.Application.Documents.Interfaces;
using GazetteAI.Application.Documents.Models;
using Microsoft.Extensions.Options;

namespace GazetteAI.Infrastructure.AI;

public sealed class GroqChatCompletionService
    : IChatCompletionService
{
    public const string DocumentAnswerNotFoundMarker =
        "[[DOCUMENT_ANSWER_NOT_FOUND]]";

    private readonly HttpClient _httpClient;
    private readonly GroqOptions _options;

    public GroqChatCompletionService(
        HttpClient httpClient,
        IOptions<GroqOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "Groq API key is missing.");
        }

        _httpClient.BaseAddress = new Uri(
            _options.BaseUrl.TrimEnd('/') + "/");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _options.ApiKey);

        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<ConversationAnalysis>
        AnalyzeConversationAsync(
            string question,
            IReadOnlyList<ChatHistoryMessage> history,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                "Question cannot be empty.",
                nameof(question));
        }

        var normalizedQuestion = question.Trim();
        var loweredQuestion =
            normalizedQuestion.ToLowerInvariant();

        var pendingGeneralQuestion =
            FindPendingGeneralQuestion(history);

        var hasPendingGeneralPermission =
            !string.IsNullOrWhiteSpace(
                pendingGeneralQuestion);

        var isPermissionReply =
            IsGeneralPermissionReply(loweredQuestion);

        var followsLiveWebAnswer =
            FollowsLiveWebAnswer(history);

        /*
         * Permission decisions are handled deterministically.
         * The model must never guess that an unclear message
         * such as "h" means yes or no.
         */
        if (hasPendingGeneralPermission &&
            isPermissionReply)
        {
            var responseLanguage =
                DetectResponseLanguage(
                    normalizedQuestion);

            if (IsNegativePermissionReply(
                    loweredQuestion))
            {
                return CreatePermissionDecisionAnalysis(
                    granted: false,
                    responseLanguage:
                        responseLanguage,
                    directResponse:
                        BuildPermissionDeclinedResponse(
                            responseLanguage));
            }

            return CreatePermissionDecisionAnalysis(
                granted: true,
                responseLanguage:
                    responseLanguage,
                directResponse: string.Empty,
                externalQuestion:
                    pendingGeneralQuestion!);
        }

        if (hasPendingGeneralPermission &&
            IsUnclearPermissionReply(
                loweredQuestion))
        {
            var responseLanguage =
                DetectResponseLanguage(
                    normalizedQuestion);

            return new ConversationAnalysis
            {
                Intent = "CasualConversation",
                RequiresDocumentSearch = false,
                DirectResponse =
                    BuildPermissionReminder(
                        responseLanguage),
                ResponseLanguage = responseLanguage,
                ResponseStyle = "standard",
                RequiresExternalKnowledge = true
            };
        }

        /*
         * Questions about GazetteAI itself must be answered from
         * known application capabilities. They are never document
         * questions and must never be sent to Tavily.
         */
        if (IsAssistantCapabilityQuestion(loweredQuestion))
        {
            var responseLanguage =
                DetectResponseLanguage(normalizedQuestion);

            return new ConversationAnalysis
            {
                Intent = "CasualConversation",
                RequiresDocumentSearch = false,
                DirectResponse = BuildCapabilityResponse(
                    normalizedQuestion,
                    responseLanguage),
                ResponseLanguage = responseLanguage,
                ResponseStyle = "standard"
            };
        }

        /*
         * A language-only message immediately after a live-web
         * answer means "translate that answer". It must not be
         * searched as a new phrase such as "tamil la sollu".
         */
        if (followsLiveWebAnswer &&
            IsLanguageCommand(loweredQuestion))
        {
            var previousAnswer =
                FindLatestAssistantAnswer(history);

            var responseLanguage =
                DetectResponseLanguage(normalizedQuestion);

            var convertedAnswer =
                await ConvertExistingAnswerAsync(
                    previousAnswer,
                    responseLanguage,
                    cancellationToken);

            return new ConversationAnalysis
            {
                Intent = "Translation",
                RequiresDocumentSearch = false,
                DirectResponse = convertedAnswer,
                ResponseLanguage = responseLanguage,
                ResponseStyle = "standard",
                IsFollowUp = true,
                IsTranslationRequest = true,
                RequiresExternalKnowledge = true
            };
        }

        /*
         * Short messages after a live-web answer are treated as
         * follow-ups to that web question. Groq rewrites them into
         * one complete query before Tavily is called again.
         */
        if (followsLiveWebAnswer &&
            !IsGreeting(loweredQuestion) &&
            !IsAcknowledgement(loweredQuestion) &&
            !IsCasualConversation(loweredQuestion))
        {
            var externalQuestion =
                await RewriteExternalFollowUpAsync(
                    normalizedQuestion,
                    history,
                    cancellationToken);

            return new ConversationAnalysis
            {
                Intent = "GeneralPermissionGranted",
                RequiresDocumentSearch = false,
                SearchQuestion = string.Empty,
                ResponseLanguage =
                    DetectResponseLanguage(
                        normalizedQuestion,
                        FindPreviousResponseLanguage(history)),
                ResponseStyle = "standard",
                IsFollowUp = true,
                RequiresExternalKnowledge = true,
                IsExternalPermissionResponse = true,
                ExternalPermissionGranted = true,
                ExternalQuestion = externalQuestion
            };
        }

        /*
         * Language-change and clarification messages can be
         * understood locally. This avoids an unnecessary Groq
         * request and prevents rate-limit problems.
         */
        if (IsLanguageCommand(loweredQuestion) ||
            IsClarificationCommand(loweredQuestion) ||
            IsGreeting(loweredQuestion) ||
            IsAcknowledgement(loweredQuestion))
        {
            return CreateLocalAnalysis(
                normalizedQuestion,
                history);
        }

        var formattedHistory =
            FormatHistory(history);

        var systemPrompt = """
            You analyze multilingual document-chat messages.

            Return only one valid JSON object in this format:

            {
              "intent": "DocumentQuestion",
              "requiresDocumentSearch": true,
              "directResponse": "",
              "searchQuestion": "standalone document question",
              "responseLanguage": "English",
              "responseStyle": "standard",
              "isClarificationRequest": false,
              "isTranslationRequest": false,
              "isFollowUp": false,
              "requiresExternalKnowledge": false,
              "isExternalPermissionResponse": false,
              "externalPermissionGranted": false
            }

            Rules:

            1. intent must be exactly one of:
               DocumentQuestion, DocumentFollowUp,
               Clarification, Translation, Acknowledgement,
               Greeting, CasualConversation, GeneralQuestion,
               GeneralPermissionGranted or GeneralPermissionDenied.

            2. requiresDocumentSearch is true only for
               DocumentQuestion, DocumentFollowUp,
               Clarification and Translation.

            3. CasualConversation is friendly social chat such
               as "how are you?" or "what about you?". Reply
               naturally in directResponse without searching
               the document.

            4. DOCUMENT-FIRST RULE: An uploaded document is
               active. Classify every substantive factual,
               explanatory, comparison, summary, recommendation,
               scope or information request as DocumentQuestion
               or DocumentFollowUp first. Do this even when the
               question could also be answered from general
               knowledge. The retrieval stage, not this analysis
               stage, decides whether the document contains the
               answer.

            5. Use GeneralQuestion only when the user explicitly
               asks for an answer outside the document, from
               general knowledge, or from the internet. Do not
               choose GeneralQuestion merely because the topic
               appears broad or unfamiliar.

            6. If PENDING GENERAL QUESTION is not "(none)",
               interpret yes, okay, sure, search, tell me, or
               equivalent multilingual replies as
               GeneralPermissionGranted. Answer the pending
               question shown there helpfully in directResponse.
               Clearly say when current/live information cannot
               be verified. Do not claim to browse the web.

            7. Interpret no, don't, வேண்டாம், epa or equivalent
               replies as GeneralPermissionDenied and respond
               politely in directResponse.

            8. For Greeting, Acknowledgement,
               CasualConversation, GeneralQuestion,
               GeneralPermissionGranted and
               GeneralPermissionDenied, directResponse must be
               a complete friendly response.

            9. searchQuestion must be a complete standalone
               question used to search the uploaded document.

            10. Use conversation history to understand
               follow-up references.

            11. Messages such as "what is its scope?",
               "what does it do?" and "mokad karanne?" are
               document follow-ups when history contains a
               document topic. Rewrite them as standalone
               document questions.

            12. Messages such as "okay", "hari", "hri okay",
               "thanks" and "got it" are acknowledgements,
               not document questions.

            13. Never answer document questions during analysis.

            14. Detect the language requested by the latest user.

            15. Tanglish means spoken Tamil written using
               Latin letters.

            16. Singlish means spoken Sinhala written using
               Latin letters.

            17. Do not mix Tanglish and Singlish.

            18. responseStyle must be:
               standard, simple or detailed.

            19. Return JSON only.
               Do not use Markdown.
            """;

        var userPrompt = $"""
            CONVERSATION HISTORY:

            {formattedHistory}

            PENDING GENERAL QUESTION:

            {pendingGeneralQuestion ?? "(none)"}

            LATEST USER MESSAGE:

            {normalizedQuestion}

            Return only the required JSON object.
            """;

        try
        {
            var response = await SendChatAsync(
                [
                    new GroqMessage
                    {
                        Role = "system",
                        Content = systemPrompt
                    },
                    new GroqMessage
                    {
                        Role = "user",
                        Content = userPrompt
                    }
                ],
                temperature: 0,
                maxCompletionTokens: 300,
                cancellationToken);

            var analysis =
                ParseAnalysis(response);

            NormalizeAnalysis(
                normalizedQuestion,
                analysis);

            ApplyLocaleHints(
                normalizedQuestion,
                history,
                analysis);

            return analysis;
        }
        catch (
            Exception exception)
            when (
                exception is JsonException ||
                exception is InvalidOperationException)
        {
            return CreateLocalAnalysis(
                normalizedQuestion,
                history);
        }
    }

    public async Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<string> contextChunks,
        IReadOnlyList<ChatHistoryMessage> history,
        ConversationAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                "Question cannot be empty.",
                nameof(question));
        }

        /*
         * Previous assistant answers are intentionally not
         * included in the final prompt. This prevents language
         * styles from leaking between messages.
         */
        _ = history;

        var responseLanguage =
            string.IsNullOrWhiteSpace(
                analysis.ResponseLanguage)
                ? "English"
                : analysis.ResponseLanguage.Trim();

        var responseStyle =
            string.IsNullOrWhiteSpace(
                analysis.ResponseStyle)
                ? "standard"
                : analysis.ResponseStyle.Trim();

        if (contextChunks.Count == 0)
        {
            return DocumentAnswerNotFoundMarker;
        }

        var context = string.Join(
            "\n\n---\n\n",
            contextChunks);

        var styleInstruction =
            BuildStyleInstruction(
                responseStyle,
                analysis.IsClarificationRequest);

        /*
         * Stage 1: Find the factual answer from the document.
         * This stage always uses English so language conversion
         * cannot incorrectly trigger the not-found response.
         */
        var groundedAnswer = await SendChatAsync(
            [
                new GroqMessage
                {
                    Role = "system",
                    Content = $"""
                        You are the factual retrieval stage of GazetteAI.

                        Answer only from DOCUMENT CONTEXT.
                        Never use outside knowledge.
                        Never invent information.

                        If the answer is not present, return exactly:
                        __NOT_FOUND__

                        Otherwise answer in clear English.
                        Do not translate the answer.
                        Do not include page numbers.
                        Do not use Markdown or HTML.

                        STYLE:
                        {styleInstruction}
                        """
                },
                new GroqMessage
                {
                    Role = "user",
                    Content = $"""
            DOCUMENT CONTEXT:

            {context}

            STANDALONE DOCUMENT QUESTION:

            {question}

            Return either the supported English answer or
            the exact marker __NOT_FOUND__.
            """
                }
            ],
            temperature: 0,
            maxCompletionTokens: 800,
            cancellationToken);

        groundedAnswer = CleanAnswer(
            groundedAnswer);

        if (groundedAnswer.Contains(
                "__NOT_FOUND__",
                StringComparison.OrdinalIgnoreCase))
        {
            return DocumentAnswerNotFoundMarker;
        }

        if (responseLanguage.Equals(
                "English",
                StringComparison.OrdinalIgnoreCase))
        {
            return groundedAnswer;
        }

        /*
         * Stage 2: Convert only the verified answer into the
         * requested language or writing style. This stage is
         * not allowed to decide whether the answer exists.
         */
        var translatedAnswer =
            await TranslateAnswerAsync(
                groundedAnswer,
                responseLanguage,
                responseStyle,
                analysis.IsClarificationRequest,
                cancellationToken);

        translatedAnswer = CleanAnswer(
            translatedAnswer);

        if (string.IsNullOrWhiteSpace(
                translatedAnswer))
        {
            throw new InvalidOperationException(
                "Groq did not return an answer.");
        }

        return translatedAnswer;
    }

    private async Task<string> TranslateAnswerAsync(
        string groundedAnswer,
        string responseLanguage,
        string responseStyle,
        bool isClarification,
        CancellationToken cancellationToken)
    {
        var languageInstruction =
            BuildLanguageInstruction(
                responseLanguage);

        var styleInstruction =
            BuildStyleInstruction(
                responseStyle,
                isClarification);

        var examples = responseLanguage
            .Trim()
            .ToLowerInvariant() switch
        {
            "tanglish" =>
                "Example style: Indha document Finland matrum " +
                "Sri Lanka education system-ai compare pannudhu.",

            "singlish" =>
                "Example style: Me document eka Finland saha " +
                "Sri Lanka adyapana kramaya compare karanawa.",

            _ => string.Empty
        };

        return await SendChatAsync(
            [
                new GroqMessage
                {
                    Role = "system",
                    Content = $"""
                        You are the language-conversion stage of
                        GazetteAI.

                        The supplied answer is already verified from
                        the document.

                        Translate or rewrite that answer only.
                        Never say that the answer was not found.
                        Do not add, remove or invent facts.
                        Preserve names and technical terms.
                        Return only the converted answer.
                        Do not use Markdown or HTML.

                        {languageInstruction}

                        {styleInstruction}

                        {examples}
                        """
                },
                new GroqMessage
                {
                    Role = "user",
                    Content = $"""
                        VERIFIED ENGLISH ANSWER:

                        {groundedAnswer}

                        Convert this answer to:
                        {responseLanguage}
                        """
                }
            ],
            temperature: 0,
            maxCompletionTokens: 300,
            cancellationToken);
    }

    public async Task<string> GenerateWebAnswerAsync(
        string question,
        IReadOnlyList<WebSearchResult> searchResults,
        ConversationAnalysis analysis,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException(
                "Question cannot be empty.",
                nameof(question));
        }

        if (searchResults.Count == 0)
        {
            return BuildWebSearchUnavailableResponse(
                analysis.ResponseLanguage);
        }

        var languageInstruction =
            BuildLanguageInstruction(
                analysis.ResponseLanguage);

        var currentDate = DateTime.UtcNow
            .ToString("yyyy-MM-dd");

        var sources = string.Join(
            "\n\n---\n\n",
            searchResults.Select((item, index) =>
                $"SOURCE {index + 1}\n" +
                $"Title: {item.Title}\n" +
                $"URL: {item.Url}\n" +
                $"Content: {item.Content}"));

        var answer = await SendChatAsync(
            [
                new GroqMessage
                {
                    Role = "system",
                    Content = $"""
                        You are GazetteAI's live-web answer
                        assistant. The user explicitly allowed an
                        answer outside the uploaded document.

                        Answer only from the supplied LIVE WEB
                        SOURCES. Never add facts from memory.
                        Today's date is {currentDate}. For current
                        office holders, laws, prices, schedules and
                        other time-sensitive facts, reject older
                        contradictory claims and use the newest
                        clearly dated authoritative evidence.
                        Prefer official and authoritative sources
                        when sources disagree. If the sources do
                        not support an answer, say that reliable
                        current information was not found.

                        Keep names, dates and official titles exact.
                        Do not claim the answer came from the PDF.
                        Do not print raw URLs because the app shows
                        source links separately. Do not use HTML.

                        {languageInstruction}
                        """
                },
                new GroqMessage
                {
                    Role = "user",
                    Content = $"""
                        QUESTION:
                        {question.Trim()}

                        LIVE WEB SOURCES:
                        {sources}
                        """
                }
            ],
            temperature: 0,
            maxCompletionTokens: 700,
            cancellationToken);

        return CleanAnswer(answer);
    }

    private async Task<string> ConvertExistingAnswerAsync(
        string existingAnswer,
        string responseLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(existingAnswer))
        {
            return BuildWebSearchUnavailableResponse(
                responseLanguage);
        }

        var languageInstruction =
            BuildLanguageInstruction(responseLanguage);

        var converted = await SendChatAsync(
            [
                new GroqMessage
                {
                    Role = "system",
                    Content = $"""
                        Convert the supplied answer into the
                        requested language. Preserve every fact,
                        name, title and date exactly. Do not add,
                        remove, correct or search for information.
                        Return only the converted answer.

                        {languageInstruction}
                        """
                },
                new GroqMessage
                {
                    Role = "user",
                    Content = existingAnswer
                }
            ],
            temperature: 0,
            maxCompletionTokens: 700,
            cancellationToken);

        return CleanAnswer(converted);
    }

    private async Task<string> RewriteExternalFollowUpAsync(
        string latestQuestion,
        IReadOnlyList<ChatHistoryMessage> history,
        CancellationToken cancellationToken)
    {
        var recentHistory = string.Join(
            "\n",
            history.TakeLast(6).Select(message =>
                $"{message.Role}: {message.Content}"));

        var rewritten = await SendChatAsync(
            [
                new GroqMessage
                {
                    Role = "system",
                    Content = """
                        Rewrite the latest user message as one
                        complete standalone web-search question.
                        Resolve pronouns, missing subjects, years
                        and short follow-up phrases from the recent
                        conversation. Preserve the user's meaning.
                        Do not answer the question. Return only the
                        rewritten question with no quotation marks.
                        """
                },
                new GroqMessage
                {
                    Role = "user",
                    Content = $"""
                        RECENT CONVERSATION:
                        {recentHistory}

                        LATEST USER MESSAGE:
                        {latestQuestion}
                        """
                }
            ],
            temperature: 0,
            maxCompletionTokens: 120,
            cancellationToken);

        var cleaned = CleanAnswer(rewritten);

        return string.IsNullOrWhiteSpace(cleaned)
            ? latestQuestion.Trim()
            : cleaned;
    }

    private static bool FollowsLiveWebAnswer(
        IReadOnlyList<ChatHistoryMessage> history)
    {
        var lastAssistantIndex = -1;

        for (var index = history.Count - 1;
             index >= 0;
             index--)
        {
            if (history[index].Role.Equals(
                    "assistant",
                    StringComparison.OrdinalIgnoreCase))
            {
                lastAssistantIndex = index;
                break;
            }
        }

        if (lastAssistantIndex < 0)
        {
            return false;
        }

        for (var index = lastAssistantIndex - 1;
             index >= 0;
             index--)
        {
            if (!history[index].Role.Equals(
                    "user",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsGeneralPermissionReply(
                    history[index].Content))
            {
                return false;
            }

            return history
                .Take(index)
                .Any(message =>
                    message.Role.Equals(
                        "assistant",
                        StringComparison.OrdinalIgnoreCase) &&
                    IsGeneralPermissionPrompt(
                        message.Content));
        }

        return false;
    }

    private static string FindLatestAssistantAnswer(
        IReadOnlyList<ChatHistoryMessage> history)
    {
        return history
            .LastOrDefault(message =>
                message.Role.Equals(
                    "assistant",
                    StringComparison.OrdinalIgnoreCase))
            ?.Content
            ?.Trim() ?? string.Empty;
    }

    private static string FindPreviousResponseLanguage(
        IReadOnlyList<ChatHistoryMessage> history)
    {
        var previousUserMessage = history
            .LastOrDefault(message =>
                message.Role.Equals(
                    "user",
                    StringComparison.OrdinalIgnoreCase))
            ?.Content;

        return string.IsNullOrWhiteSpace(previousUserMessage)
            ? "English"
            : DetectResponseLanguage(previousUserMessage);
    }

    private async Task<string> SendChatAsync(
        IReadOnlyList<GroqMessage> messages,
        double temperature,
        int maxCompletionTokens,
        CancellationToken cancellationToken)
    {
        var request = new GroqChatRequest
        {
            Model = _options.ChatModel,
            Messages = messages.ToList(),
            Temperature = temperature,
            MaxCompletionTokens =
                maxCompletionTokens
        };

        using var response =
            await _httpClient.PostAsJsonAsync(
                "chat/completions",
                request,
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error =
                await response.Content
                    .ReadAsStringAsync(
                        cancellationToken);

            throw new InvalidOperationException(
                $"Groq chat request failed: " +
                $"{(int)response.StatusCode} " +
                $"{response.StatusCode}. {error}");
        }

        var result =
            await response.Content
                .ReadFromJsonAsync<GroqChatResponse>(
                    cancellationToken:
                        cancellationToken);

        var content = result?
            .Choices
            .FirstOrDefault()?
            .Message
            .Content?
            .Trim();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                "Groq did not return content.");
        }

        return content;
    }

    private static ConversationAnalysis
        CreateLocalAnalysis(
            string question,
            IReadOnlyList<ChatHistoryMessage> history)
    {
        var loweredQuestion =
            question.Trim().ToLowerInvariant();

        var isLanguageRequest =
            IsLanguageCommand(loweredQuestion);

        var isClarification =
            IsClarificationCommand(
                loweredQuestion);

        var isGreeting =
            IsGreeting(loweredQuestion);

        var isAcknowledgement =
            IsAcknowledgement(loweredQuestion);

        var requiresDocumentSearch =
            !isGreeting &&
            !isAcknowledgement;

        var searchQuestion =
            isLanguageRequest || isClarification
                ? FindPreviousRealQuestion(
                    question,
                    history)
                : question.Trim();

        var analysis = new ConversationAnalysis
        {
            Intent = isGreeting
                ? "Greeting"
                : isAcknowledgement
                    ? "Acknowledgement"
                    : isLanguageRequest
                        ? "Translation"
                        : isClarification
                            ? "Clarification"
                            : history.Count > 0
                                ? "DocumentFollowUp"
                                : "DocumentQuestion",

            RequiresDocumentSearch =
                requiresDocumentSearch,

            DirectResponse =
                requiresDocumentSearch
                    ? string.Empty
                    : BuildDirectResponse(
                        question,
                        isGreeting),

            SearchQuestion = searchQuestion,
            ResponseLanguage =
                DetectResponseLanguage(question),

            ResponseStyle =
                isClarification
                    ? "simple"
                    : "standard",

            IsClarificationRequest =
                isClarification,

            IsClarification =
                isClarification,

            IsTranslationRequest =
                isLanguageRequest,

            IsFollowUp =
                isLanguageRequest ||
                isClarification,

            ResponseInstruction =
                string.Empty
        };

        return analysis;
    }

    private static void ApplyLocaleHints(
        string question,
        IReadOnlyList<ChatHistoryMessage> history,
        ConversationAnalysis analysis)
    {
        var normalized =
            question.Trim().ToLowerInvariant();

        var languageCommand =
            IsLanguageCommand(normalized);

        var clarificationCommand =
            IsClarificationCommand(normalized);

        var greeting =
            IsGreeting(normalized);

        var acknowledgement =
            IsAcknowledgement(normalized);

        if (greeting || acknowledgement)
        {
            analysis.Intent = greeting
                ? "Greeting"
                : "Acknowledgement";

            analysis.RequiresDocumentSearch = false;
            analysis.DirectResponse =
                BuildDirectResponse(
                    question,
                    greeting);

            analysis.SearchQuestion = string.Empty;
            analysis.IsFollowUp = false;
            analysis.IsClarificationRequest = false;
            analysis.IsTranslationRequest = false;
        }

        if (languageCommand ||
            clarificationCommand)
        {
            analysis.SearchQuestion =
                FindPreviousRealQuestion(
                    question,
                    history);

            analysis.IsFollowUp = true;
        }

        analysis.ResponseLanguage =
            DetectResponseLanguage(
                question,
                analysis.ResponseLanguage);

        if (languageCommand)
        {
            analysis.Intent = "Translation";
            analysis.RequiresDocumentSearch = true;
            analysis.DirectResponse = string.Empty;
            analysis.IsTranslationRequest = true;
        }

        if (clarificationCommand)
        {
            analysis.Intent = "Clarification";
            analysis.RequiresDocumentSearch = true;
            analysis.DirectResponse = string.Empty;
            analysis.IsClarificationRequest = true;
            analysis.IsClarification = true;
            analysis.ResponseStyle = "simple";

            /*
             * Latin-letter Tamil clarification commands
             * should receive a Tanglish response.
             */
            if (IsTanglishClarification(normalized))
            {
                analysis.ResponseLanguage =
                    "Tanglish";
            }
        }

        if (ContainsAny(
                normalized,
                "detail ah",
                "detailed",
                "explain fully",
                "more detail"))
        {
            analysis.ResponseStyle =
                "detailed";
        }

        analysis.DetectedLanguage =
            analysis.ResponseLanguage;

        analysis.WritingStyle =
            analysis.ResponseStyle;
    }

    private static string DetectResponseLanguage(
        string question,
        string defaultLanguage = "English")
    {
        var normalized =
            question.Trim().ToLowerInvariant();

        if (ContainsAny(
                normalized,
                "singlish"))
        {
            return "Singlish";
        }

        if (ContainsAny(
                normalized,
                "tanglish"))
        {
            return "Tanglish";
        }

        if (ContainsAny(
                normalized,
                "tamil la",
                "tamill la",
                "tamil-la",
                "tamill-la",
                "thamizh la",
                "thamizh-la",
                "answer in tamil",
                "தமிழில்",
                "தமிழ்"))
        {
            return "Tamil";
        }

        if (ContainsAny(
                normalized,
                "sinhala walin",
                "sinhalen",
                "answer in sinhala",
                "සිංහලෙන්",
                "සිංහල"))
        {
            return "Sinhala";
        }

        if (ContainsAny(
                normalized,
                "français",
                "french",
                "en français"))
        {
            return "French";
        }

        if (ContainsAny(
                normalized,
                "hindi",
                "हिंदी"))
        {
            return "Hindi";
        }

        if (ContainsAny(
                normalized,
                "in english",
                "english la",
                "english walin",
                "answer in english"))
        {
            return "English";
        }

        if (ContainsTamilCharacters(question))
        {
            return "Tamil";
        }

        if (ContainsSinhalaCharacters(question))
        {
            return "Sinhala";
        }

        if (ContainsAny(
                normalized,
                "mokada",
                "mokakda",
                "mokad karanne",
                "monawada",
                "kohomada",
                "hari",
                "hri",
                "kiyanna",
                "karanne",
                "eka mokakda"))
        {
            return "Singlish";
        }

        if (ContainsAny(
                normalized,
                "puriyala",
                "purila",
                "simple ah sollu",
                "easy ah sollu",
                "thelivaga sollu",
                "enna ",
                "eanna ",
                "sollu",
                "solllu",
                "mudium",
                "therium",
                "evlo",
                "eavalo",
                "parthu",
                "pathu"))
        {
            return "Tanglish";
        }

        return string.IsNullOrWhiteSpace(
            defaultLanguage)
                ? "English"
                : defaultLanguage;
    }

    private static string FindPreviousRealQuestion(
        string latestQuestion,
        IReadOnlyList<ChatHistoryMessage> history)
    {
        for (var index = history.Count - 1;
             index >= 0;
             index--)
        {
            var message = history[index];

            if (!message.Role.Equals(
                    "user",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content =
                message.Content?.Trim();

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var normalized =
                content.ToLowerInvariant();

            if (IsLanguageCommand(normalized) ||
                IsClarificationCommand(normalized) ||
                IsGreeting(normalized) ||
                IsAcknowledgement(normalized) ||
                IsCasualConversation(normalized) ||
                IsGeneralPermissionReply(normalized))
            {
                continue;
            }

            return content;
        }

        return latestQuestion.Trim();
    }

    private static bool IsLanguageCommand(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        // Stand-alone language names are valid commands.
        if (normalized is
            "tamil" or
            "tamill" or
            "thamizh" or
            "தமிழ்" or
            "english" or
            "sinhala" or
            "සිංහල" or
            "singlish" or
            "tanglish" or
            "french" or
            "français" or
            "hindi" or
            "हिंदी")
        {
            return true;
        }

        var mentionsLanguage = ContainsAny(
            normalized,
            "tamil",
            "tamill",
            "thamizh",
            "தமிழ்",
            "english",
            "sinhala",
            "සිංහල",
            "singlish",
            "tanglish",
            "french",
            "français",
            "hindi",
            "हिंदी");

        var asksForResponse = ContainsAny(
            normalized,
            "sollu",
            "solllu",
            "sollunga",
            "sollungal",
            "kiyanna",
            "walin",
            "answer in",
            "reply in",
            "respond in",
            "in english",
            "en français",
            "répondez",
            "mein bata",
            "में बताइए");

        return mentionsLanguage && asksForResponse;
    }

    private static bool IsClarificationCommand(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        if (normalized is
            "enna" or
            "eanna" or
            "enna sollura" or
            "eanna sollura")
        {
            return true;
        }

        return ContainsAny(
            normalized,
            "puriyala",
            "puriyalaa",
            "purila",
            "puriyavillai",
            "simple ah sollu",
            "simple-a sollu",
            "easy ah sollu",
            "theliva sollu",
            "thelivaa sollu",
            "thelivaga sollu",
            "தெளிவாக சொல்லுங்கள்",
            "எளிமையாக சொல்லுங்கள்",
            "புரியவில்லை",
            "therenne naha",
            "තේරෙන්නේ නැහැ",
            "simple walin kiyanna",
            "explain clearly",
            "explain simply",
            "i don't understand",
            "i do not understand");
    }

    private static bool IsTanglishClarification(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        return normalized is
                   "enna" or
                   "eanna" or
                   "enna sollura" or
                   "eanna sollura" ||
               ContainsAny(
                   normalized,
                   "puriyala",
                   "puriyalaa",
                   "purila",
                   "puriyavillai",
                   "simple ah sollu",
                   "simple-a sollu",
                   "easy ah sollu",
                   "theliva sollu",
                   "thelivaa sollu",
                   "thelivaga sollu");
    }

    private static bool IsGreeting(
        string text)
    {
        var normalized =
            NormalizeShortMessage(text);

        return normalized is
            "hi" or
            "hello" or
            "hey" or
            "hi buddy" or
            "hello buddy" or
            "good morning" or
            "good afternoon" or
            "good evening" or
            "vanakkam" or
            "வணக்கம்" or
            "ayubowan" or
            "ආයුබෝවන්";
    }

    private static bool IsAcknowledgement(
        string text)
    {
        var normalized =
            NormalizeShortMessage(text);

        return normalized is
            "ok" or
            "okay" or
            "ok buddy" or
            "okay buddy" or
            "hari" or
            "hari okay" or
            "hri" or
            "hri okay" or
            "ela" or
            "thanks" or
            "thank you" or
            "thanks buddy" or
            "got it" or
            "understood" or
            "சரி" or
            "சரி நன்றி" or
            "நன்றி" or
            "හරි" or
            "ස්තුතියි";
    }

    private static string NormalizeShortMessage(
        string text)
    {
        return text
            .Trim()
            .Trim('.', ',', '!', '?', ';', ':')
            .ToLowerInvariant();
    }

    private static string BuildDirectResponse(
     string question,
     bool isGreeting)
    {
        var normalized =
            NormalizeShortMessage(question);

        var language =
            DetectResponseLanguage(question);

        var isThanks =
            ContainsAny(
                normalized,
                "thanks",
                "thank you",
                "thanks buddy",
                "நன்றி",
                "ස්තුතියි");

        if (isThanks)
        {
            return language.Trim().ToLowerInvariant()
                switch
            {
                "tamil" =>
                    "வரவேற்கிறேன்! வேறு ஏதாவது உதவி வேண்டுமா?",

                "tanglish" =>
                    "Welcome! Vera edhavadhu help venuma?",

                "sinhala" =>
                    "ඔබව සාදරයෙන් පිළිගන්නවා! තවත් උදව්වක් අවශ්‍යද?",

                "singlish" =>
                    "Welcome! Thawa monawada help one?",

                "french" =>
                    "Avec plaisir ! Puis-je vous aider autrement ?",

                "hindi" =>
                    "आपका स्वागत है! क्या आपको कोई और सहायता चाहिए?",

                _ =>
                    "You’re welcome! Is there anything else I can help with?"
            };
        }

        return language.Trim().ToLowerInvariant()
            switch
        {
            "tamil" => isGreeting
                ? "வணக்கம்! இந்த ஆவணத்தைப் பற்றி என்ன தெரிந்துகொள்ள விரும்புகிறீர்கள்?"
                : "சரி! இந்த ஆவணத்தைப் பற்றி வேறு என்ன தெரிந்துகொள்ள விரும்புகிறீர்கள்?",

            "tanglish" => isGreeting
                ? "Vanakkam! Indha document pathi enna therinjukkanum?"
                : "Sari! Indha document pathi vera enna therinjukkanum?",

            "sinhala" => isGreeting
                ? "ආයුබෝවන්! මෙම ලේඛනය ගැන ඔබට දැනගන්න අවශ්‍ය කුමක්ද?"
                : "හරි! මෙම ලේඛනය ගැන තවත් මොනවාද දැනගන්න අවශ්‍ය?",

            "singlish" => isGreeting
                ? "Ayubowan! Me document eka gena monawada danaganna one?"
                : "Hari! Me document eka gena thawa monawada danaganna one?",

            "french" => isGreeting
                ? "Bonjour ! Que souhaitez-vous savoir sur ce document ?"
                : "D’accord ! Que souhaitez-vous savoir d’autre sur ce document ?",

            "hindi" => isGreeting
                ? "नमस्ते! आप इस दस्तावेज़ के बारे में क्या जानना चाहते हैं?"
                : "ठीक है! आप इस दस्तावेज़ के बारे में और क्या जानना चाहते हैं?",

            _ => isGreeting
                ? "Hello! What would you like to know about this document?"
                : "Okay! What else would you like to know about this document?"
        };
    }

    private static string BuildLanguageInstruction(
        string language)
    {
        return language.Trim().ToLowerInvariant()
            switch
        {
            "tamil" =>
                """
                    Write the complete answer using Tamil script.
                    Do not write the answer in English, Tanglish
                    or Sinhala.
                    """,

            "tanglish" =>
                """
                    Write the complete answer in natural spoken
                    Tamil using only Latin letters.
                    Do not use Tamil or Sinhala script.
                    Do not use Sinhala vocabulary.
                    """,

            "sinhala" =>
                """
                    Write the complete answer using Sinhala script.
                    Do not use Tamil script or Singlish.
                    """,

            "singlish" =>
                """
                    Write the complete answer in natural spoken
                    Sinhala using only Latin letters.
                    Do not use Tamil or Tanglish vocabulary.
                    Do not explain what Singlish means.
                    """,

            "french" =>
                """
                    Write the complete answer naturally in French.
                    """,

            "hindi" =>
                """
                    Write the complete answer naturally in Hindi
                    using Devanagari script.
                    """,

            _ =>
                """
                    Write the complete answer in natural English.
                    """
        };
    }

    private static string BuildStyleInstruction(
        string style,
        bool isClarification)
    {
        if (isClarification ||
            style.Equals(
                "simple",
                StringComparison.OrdinalIgnoreCase))
        {
            return """
                Give a very simple and friendly explanation.
                Use short sentences and easy words.
                Answer only the requested information.
                """;
        }

        if (style.Equals(
                "detailed",
                StringComparison.OrdinalIgnoreCase))
        {
            return """
                Give a clear, structured and detailed answer.
                Include only information supported by the context.
                """;
        }

        return """
            Give a short, direct and natural answer.
            """;
    }

    private static string BuildNotFoundMessage(
        string? language)
    {
        return language?
            .Trim()
            .ToLowerInvariant() switch
        {
            "tamil" =>
                "இந்த ஆவணத்தில் பதில் காணப்படவில்லை.",

            "tanglish" =>
                "Indha document-la badhil kidaikkala.",

            "sinhala" =>
                "මෙම ලේඛනයේ පිළිතුර සඳහන් කර නැහැ.",

            "singlish" =>
                "Me document eke uththaraya sandahan wela naha.",

            "french" =>
                "La réponse ne figure pas dans ce document.",

            "hindi" =>
                "इस दस्तावेज़ में उत्तर नहीं मिला।",

            _ =>
                "The answer was not found in this document."
        };
    }

    private static ConversationAnalysis ParseAnalysis(
        string response)
    {
        var json = response.Trim();

        if (json.StartsWith(
                "```",
                StringComparison.Ordinal))
        {
            var firstLineEnd =
                json.IndexOf('\n');

            if (firstLineEnd >= 0)
            {
                json =
                    json[(firstLineEnd + 1)..];
            }

            var closingFence =
                json.LastIndexOf(
                    "```",
                    StringComparison.Ordinal);

            if (closingFence >= 0)
            {
                json = json[..closingFence];
            }
        }

        var firstBrace = json.IndexOf('{');
        var lastBrace = json.LastIndexOf('}');

        if (firstBrace >= 0 &&
            lastBrace > firstBrace)
        {
            json =
                json[firstBrace..(lastBrace + 1)];
        }

        var analysis =
            JsonSerializer.Deserialize<
                ConversationAnalysis>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive =
                        true
                });

        return analysis ??
               throw new JsonException(
                   "Conversation analysis was empty.");
    }

    private static void NormalizeAnalysis(
        string question,
        ConversationAnalysis analysis)
    {
        var validIntents = new[]
        {
            "DocumentQuestion",
            "DocumentFollowUp",
            "Clarification",
            "Translation",
            "Acknowledgement",
            "Greeting",
            "CasualConversation",
            "GeneralQuestion",
            "GeneralPermissionGranted",
            "GeneralPermissionDenied"
        };

        if (string.IsNullOrWhiteSpace(
                analysis.Intent) ||
            !validIntents.Contains(
                analysis.Intent,
                StringComparer.OrdinalIgnoreCase))
        {
            analysis.Intent =
                "DocumentQuestion";
        }
        else
        {
            analysis.Intent = validIntents
                .First(intent => intent.Equals(
                    analysis.Intent,
                    StringComparison.OrdinalIgnoreCase));
        }

        /*
         * Deterministic document-first guard. A broad question
         * must still be searched in the uploaded PDF unless the
         * user explicitly asks for outside/general information.
         */
        if (analysis.Intent == "GeneralQuestion" &&
            !IsExplicitExternalRequest(question))
        {
            analysis.Intent = "DocumentQuestion";
        }

        var isDirectIntent =
            analysis.Intent == "Greeting" ||
            analysis.Intent == "Acknowledgement" ||
            analysis.Intent == "CasualConversation" ||
            analysis.Intent == "GeneralQuestion" ||
            analysis.Intent == "GeneralPermissionGranted" ||
            analysis.Intent == "GeneralPermissionDenied";

        analysis.RequiresDocumentSearch =
            !isDirectIntent;

        if (isDirectIntent)
        {
            if (string.IsNullOrWhiteSpace(
                    analysis.DirectResponse) &&
                (analysis.Intent == "Greeting" ||
                 analysis.Intent == "Acknowledgement"))
            {
                analysis.DirectResponse =
                    BuildDirectResponse(
                        question,
                        analysis.Intent == "Greeting");
            }

            if (string.IsNullOrWhiteSpace(
                    analysis.DirectResponse) &&
                analysis.Intent == "CasualConversation")
            {
                analysis.DirectResponse =
                    BuildCasualResponse(
                        question,
                        analysis.ResponseLanguage);
            }

            if (analysis.Intent == "GeneralQuestion")
            {
                analysis.DirectResponse =
                    BuildGeneralPermissionPrompt(
                        analysis.ResponseLanguage);
            }

            if (string.IsNullOrWhiteSpace(
                    analysis.DirectResponse) &&
                analysis.Intent == "GeneralPermissionDenied")
            {
                analysis.DirectResponse =
                    "Okay. I will continue using only the " +
                    "uploaded document.";
            }

            if (string.IsNullOrWhiteSpace(
                    analysis.DirectResponse) &&
                analysis.Intent == "GeneralPermissionGranted")
            {
                analysis.DirectResponse =
                    "Please repeat the general question, and " +
                    "I’ll help you with it.";
            }

            analysis.SearchQuestion = string.Empty;
            analysis.IsFollowUp = false;
            analysis.IsClarificationRequest = false;
            analysis.IsTranslationRequest = false;

            analysis.RequiresExternalKnowledge =
                analysis.Intent == "GeneralQuestion";

            analysis.IsExternalPermissionResponse =
                analysis.Intent == "GeneralPermissionGranted" ||
                analysis.Intent == "GeneralPermissionDenied";

            analysis.ExternalPermissionGranted =
                analysis.Intent == "GeneralPermissionGranted";
        }
        else
        {
            analysis.DirectResponse = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(
                analysis.SearchQuestion) &&
            analysis.RequiresDocumentSearch)
        {
            analysis.SearchQuestion =
                question.Trim();
        }

        if (string.IsNullOrWhiteSpace(
                analysis.ResponseLanguage))
        {
            analysis.ResponseLanguage =
                "English";
        }

        if (string.IsNullOrWhiteSpace(
                analysis.ResponseStyle))
        {
            analysis.ResponseStyle =
                "standard";
        }

        analysis.SearchQuestion =
            analysis.SearchQuestion.Trim();

        analysis.DirectResponse =
            analysis.DirectResponse.Trim();

        analysis.ResponseLanguage =
            analysis.ResponseLanguage.Trim();

        analysis.ResponseStyle =
            analysis.ResponseStyle
                .Trim()
                .ToLowerInvariant();

        if (analysis.ResponseStyle != "standard" &&
            analysis.ResponseStyle != "simple" &&
            analysis.ResponseStyle != "detailed")
        {
            analysis.ResponseStyle =
                "standard";
        }

        analysis.DetectedLanguage =
            analysis.ResponseLanguage;

        analysis.WritingStyle =
            analysis.ResponseStyle;

        if (analysis.Intent == "Clarification")
        {
            analysis.IsClarificationRequest = true;
            analysis.IsFollowUp = true;
        }

        if (analysis.Intent == "Translation")
        {
            analysis.IsTranslationRequest = true;
            analysis.IsFollowUp = true;
        }

        if (analysis.Intent == "DocumentFollowUp")
        {
            analysis.IsFollowUp = true;
        }
    }

    private static string? FindPendingGeneralQuestion(
        IReadOnlyList<ChatHistoryMessage> history)
    {
        for (var index = history.Count - 1;
             index >= 0;
             index--)
        {
            var message = history[index];

            if (!message.Role.Equals(
                    "assistant",
                    StringComparison.OrdinalIgnoreCase) ||
                !IsGeneralPermissionPrompt(
                    message.Content))
            {
                continue;
            }

            /*
             * If the user already replied to this permission
             * prompt, it is no longer pending.
             */
            var alreadyResolved = history
                .Skip(index + 1)
                .Any(laterMessage =>
                    laterMessage.Role.Equals(
                        "user",
                        StringComparison.OrdinalIgnoreCase) &&
                    IsGeneralPermissionReply(
                        laterMessage.Content));

            if (alreadyResolved)
            {
                return null;
            }

            for (var questionIndex = index - 1;
                 questionIndex >= 0;
                 questionIndex--)
            {
                var possibleQuestion =
                    history[questionIndex];

                if (!possibleQuestion.Role.Equals(
                        "user",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(
                        possibleQuestion.Content))
                {
                    continue;
                }

                var normalizedCandidate =
                    possibleQuestion.Content
                        .Trim()
                        .ToLowerInvariant();

                if (IsUnclearPermissionReply(
                        normalizedCandidate) ||
                    IsGreeting(normalizedCandidate) ||
                    IsAcknowledgement(normalizedCandidate) ||
                    IsCasualConversation(
                        normalizedCandidate))
                {
                    continue;
                }

                return possibleQuestion.Content.Trim();
            }

            return null;
        }

        return null;
    }

    private static bool IsGeneralPermissionPrompt(
        string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return ContainsAny(
            text.ToLowerInvariant(),
            "general ai knowledge",
            "general knowledge",
            "outside the document",
            "ஆவணத்திற்கு வெளியே",
            "பொதுவான அறிவைப்",
            "பொதுவான ai அறிவைப்",
            "document ekata pitin",
            "document eken pita",
            "සාමාන්‍ය ai දැනුම",
            "connaissances générales",
            "सामान्य ai ज्ञान");
    }

    private static bool IsCasualConversation(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        return ContainsAny(
            normalized,
            "how are you",
            "how about you",
            "what about you",
            "what about to you",
            "who are you",
            "what are you",
            "what can you do",
            "how can you help",
            "what help can you",
            "enna help",
            "eanna help",
            "enna panna mudium",
            "eanna panna mudium",
            "உன்னால் என்ன",
            "நீ யார்",
            "ඔයා කවුද");
    }

    private static bool IsAssistantCapabilityQuestion(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        return IsPdfImageCapabilityQuestion(normalized) ||
            ContainsAny(
            normalized,
            "what can you do",
            "how can you help",
            "enna help",
            "eanna help",
            "enna panna mudium",
            "eanna panna mudium",
            "pdf ulla photo",
            "pdf la photo",
            "pdf image",
            "images in pdf",
            "photo parthu",
            "photo pathu",
            "படங்களை பார்க்க",
            "படம் பார்க்க",
            "maximum pdf",
            "max pdf",
            "pdf size",
            "how big pdf",
            "evlo periya pdf",
            "eavalo periya pdf",
            "எவ்வளவு பெரிய pdf",
            "what languages",
            "which languages",
            "languages do you know",
            "language therium",
            "languages therium",
            "therinja language",
            "therinja langvage",
            "enna enna moli",
            "eanna eanna moli",
            "என்ன மொழிகள்",
            "எந்த மொழிகள்");
    }

    private static string BuildCapabilityResponse(
        string question,
        string? language)
    {
        var normalized = NormalizeShortMessage(question);
        var requestedLanguage = language?
            .Trim()
            .ToLowerInvariant() ?? "english";

        var asksAboutImages =
            IsPdfImageCapabilityQuestion(normalized);

        if (asksAboutImages)
        {
            return requestedLanguage switch
            {
                "tamil" =>
                    "PDF-ல் உள்ள ஸ்கேன் செய்யப்பட்ட படங்களில் " +
                    "இருக்கும் தமிழ் மற்றும் ஆங்கில எழுத்துகளை OCR " +
                    "மூலம் படிக்க முடியும். ஆனால் புகைப்படத்தில் உள்ள " +
                    "நபர்கள், பொருட்கள் அல்லது காட்சிகளை visual AI " +
                    "போல் இன்னும் புரிந்து விவரிக்க முடியாது.",
                "tanglish" =>
                    "PDF-la scan image-kulla irukkira Tamil/English " +
                    "text-ai OCR moolama read panna mudiyum. Aana " +
                    "photo-la irukkira person, object, scene-ai visual " +
                    "AI madhiri innum understand panni describe panna " +
                    "mudiyadhu.",
                "singlish" =>
                    "PDF scan image eke thiyena Tamil/English text OCR " +
                    "walin read karanna puluwan. Habai photo eke people, " +
                    "objects saha scene visual AI wage describe karanna " +
                    "thama baha.",
                _ =>
                    "I can use OCR to read Tamil and English text inside " +
                    "scanned PDF images. I cannot yet visually identify " +
                    "and describe people, objects or scenes in photos."
            };
        }

        var asksAboutPdfSize = ContainsAny(
            normalized,
            "maximum pdf",
            "max pdf",
            "pdf size",
            "how big pdf",
            "evlo periya pdf",
            "eavalo periya pdf",
            "எவ்வளவு பெரிய pdf");

        if (asksAboutPdfSize)
        {
            return requestedLanguage switch
            {
                "tamil" =>
                    "தற்போது ஒரு PDF-க்கு அதிகபட்சமாக சுமார் 20 MB " +
                    "வரை upload செய்யலாம். மிகப் பெரிய PDF என்றால் அதை " +
                    "சிறிய பகுதிகளாகப் பிரித்து upload செய்வது நல்லது.",
                "tanglish" =>
                    "Ippo oru PDF maximum-a approximately 20 MB varaikkum " +
                    "upload panna mudiyum. Romba periya PDF-na small parts-a " +
                    "split panni upload pannunga.",
                "singlish" =>
                    "Dan eka PDF ekak approximately 20 MB wenakan upload " +
                    "karanna puluwan. Loku PDF ekak nam podi kotas walata " +
                    "split karala upload karanna.",
                _ =>
                    "The current upload limit is approximately 20 MB per " +
                    "PDF. For larger documents, split the PDF into smaller " +
                    "parts before uploading."
            };
        }

        var asksAboutLanguages = ContainsAny(
            normalized,
            "what languages",
            "which languages",
            "languages do you know",
            "language therium",
            "languages therium",
            "therinja language",
            "therinja langvage",
            "enna enna moli",
            "eanna eanna moli",
            "என்ன மொழிகள்",
            "எந்த மொழிகள்");

        if (asksAboutLanguages)
        {
            return requestedLanguage switch
            {
                "tamil" =>
                    "தமிழ், ஆங்கிலம், சிங்களம், Tanglish, Singlish, " +
                    "பிரெஞ்சு மற்றும் ஹிந்தி உட்பட பல மொழிகளில் உங்கள் " +
                    "கேள்விகளைப் புரிந்து பதில் சொல்ல முடியும்.",
                "tanglish" =>
                    "Tamil, English, Sinhala, Tanglish, Singlish, French, " +
                    "Hindi including pala languages-la unga questions-ai " +
                    "understand panni answer solla mudiyum.",
                "singlish" =>
                    "Tamil, English, Sinhala, Tanglish, Singlish, French saha " +
                    "Hindi wage languages godak therum aran answer karanna " +
                    "puluwan.",
                _ =>
                    "I can understand and answer in many languages, including " +
                    "Tamil, English, Sinhala, Tanglish, Singlish, French and Hindi."
            };
        }

        return BuildCasualResponse(question, language);
    }

    private static bool IsPdfImageCapabilityQuestion(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        var mentionsPdf = ContainsAny(
            normalized,
            "pdf",
            "document",
            "ஆவணம்");

        var mentionsImage = ContainsAny(
            normalized,
            "photo",
            "photos",
            "image",
            "images",
            "படம்",
            "படங்களை",
            "புகைப்படம்");

        var asksToInspect = ContainsAny(
            normalized,
            "parthu",
            "pathu",
            "paarka",
            "read",
            "see",
            "view",
            "analyse",
            "analyze",
            "describe",
            "பார்க்க",
            "படிக்க",
            "விவரிக்க",
            "முடியுமா");

        return mentionsPdf &&
               mentionsImage &&
               asksToInspect;
    }

    private static bool IsExplicitExternalRequest(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        return ContainsAny(
            normalized,
            "outside this document",
            "outside the document",
            "beyond this document",
            "from general knowledge",
            "using general knowledge",
            "search the internet",
            "search online",
            "web search",
            "document-ku veliya",
            "document la illama",
            "ஆவணத்திற்கு வெளியே",
            "இணையத்தில் தேடு");
    }

    private static string BuildGeneralPermissionPrompt(
        string? language)
    {
        return language?
            .Trim()
            .ToLowerInvariant() switch
        {
            "tamil" =>
                "இந்தக் கேள்வி பதிவேற்றிய ஆவணத்துடன் " +
                "தொடர்புடையது அல்ல. பொதுவான AI அறிவைப் " +
                "பயன்படுத்தி பதில் சொல்லவா?",

            "tanglish" =>
                "Indha question uploaded document-oda " +
                "related illa. General AI knowledge use " +
                "panni answer sollava?",

            "sinhala" =>
                "මෙම ප්‍රශ්නය උඩුගත කළ ලේඛනයට අදාළ නැහැ. " +
                "සාමාන්‍ය AI දැනුමෙන් පිළිතුරු දෙන්නද?",

            "singlish" =>
                "Me question eka uploaded document ekata " +
                "related naha. General AI knowledge use " +
                "karala answer karannada?",

            "french" =>
                "Cette question ne concerne pas le document. " +
                "Voulez-vous une réponse basée sur les " +
                "connaissances générales de l’IA ?",

            "hindi" =>
                "यह प्रश्न अपलोड किए गए दस्तावेज़ से संबंधित " +
                "नहीं है। क्या मैं सामान्य AI ज्ञान से उत्तर दूँ?",

            _ =>
                "This question is not related to the uploaded " +
                "document. Would you like an answer using " +
                "general AI knowledge?"
        };
    }

    private static string BuildCasualResponse(
        string question,
        string? language)
    {
        var normalized = NormalizeShortMessage(question);
        var asksAboutAssistant = ContainsAny(
            normalized,
            "about you",
            "who are you",
            "what are you",
            "உன்னை பற்றி",
            "ඔයා කවුද");

        if (asksAboutAssistant)
        {
            return language?
                .Trim()
                .ToLowerInvariant() switch
            {
                "tamil" =>
                    "நான் GazetteAI. உங்கள் ஆவணங்களைப் " +
                    "புரிந்துகொண்டு கேள்விகளுக்கு பதில் " +
                    "சொல்லும் AI உதவியாளர்.",

                "tanglish" =>
                    "Naan GazetteAI. Unga documents-ai " +
                    "understand panni questions-ku answer " +
                    "sollura AI assistant.",

                "singlish" =>
                    "Mama GazetteAI. Oyage documents " +
                    "therum aran questions walata answer " +
                    "karana AI assistant kenek.",

                _ =>
                    "I’m GazetteAI, a friendly AI assistant " +
                    "that understands your documents and " +
                    "answers your questions."
            };
        }

        return "I’m doing well, thank you! How can I help you?";
    }

    private static bool IsGeneralPermissionReply(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        if (normalized is
            "yes" or
            "yeah" or
            "okay" or
            "ok" or
            "sure" or
            "tell me" or
            "search" or
            "sollu" or
            "solllu" or
            "ஆம்" or
            "சொல்லு" or
            "வேண்டாம்" or
            "no" or
            "no thanks" or
            "don't" or
            "dont" or
            "epa" or
            "kiyanna")
        {
            return true;
        }

        return normalized.StartsWith(
                   "yes ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "yes,",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "okay ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "okay,",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ok ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ok,",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "sure ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ஆம் ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ஆம்,",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ஆமாம் ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "சரி ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ow ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "hari ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "ඔව් ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "oui ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "हाँ ",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(
                   "no ",
                   StringComparison.OrdinalIgnoreCase) ||
               ContainsAny(
                   normalized,
                   "general knowledge use",
                   "outside answer",
                   "வெளியில் இருந்து சொல்லு",
                   "இணையத்தில் தேடி",
                   "இணையத்தில் தேடு",
                   "web la search",
                   "internet la search",
                   "web eken hoyala",
                   "internet eken hoyala");
    }

    private static bool IsNegativePermissionReply(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        return normalized is
                   "no" or
                   "no thanks" or
                   "don't" or
                   "dont" or
                   "epa" or
                   "வேண்டாம்" ||
               normalized.StartsWith(
                   "no ",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnclearPermissionReply(
        string text)
    {
        var normalized = NormalizeShortMessage(text);

        return normalized is
            "h" or
            "hm" or
            "hmm" or
            "mmm" or
            "?";
    }

    private static ConversationAnalysis
        CreatePermissionDecisionAnalysis(
            bool granted,
            string responseLanguage,
            string directResponse,
            string externalQuestion = "")
    {
        return new ConversationAnalysis
        {
            Intent = granted
                ? "GeneralPermissionGranted"
                : "GeneralPermissionDenied",

            RequiresDocumentSearch = false,
            DirectResponse = directResponse,
            SearchQuestion = string.Empty,
            ResponseLanguage = responseLanguage,
            ResponseStyle = "standard",
            IsFollowUp = true,
            RequiresExternalKnowledge = granted,
            IsExternalPermissionResponse = true,
            ExternalPermissionGranted = granted,
            ExternalQuestion = externalQuestion
        };
    }

    private static string BuildWebSearchUnavailableResponse(
        string? language)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "tamil" =>
                "மன்னிக்கவும், நம்பகமான தற்போதைய தகவலை " +
                "இணையத்தில் கண்டுபிடிக்க முடியவில்லை.",
            "tanglish" =>
                "Sorry, reliable current information web-la " +
                "kidaikkala.",
            "singlish" =>
                "Sorry, reliable current information web eken " +
                "hoya ganna bari una.",
            _ =>
                "Sorry, I couldn’t find reliable current " +
                "information on the web."
        };
    }

    private static string BuildPermissionReminder(
        string? language)
    {
        return language?
            .Trim()
            .ToLowerInvariant() switch
        {
            "tamil" =>
                "உங்கள் பதில் தெளிவாக இல்லை. பொதுவான AI " +
                "அறிவைப் பயன்படுத்த வேண்டுமா? ஆம் அல்லது " +
                "வேண்டாம் என்று சொல்லுங்கள்.",

            "tanglish" =>
                "Unga reply clear-a illa. General AI knowledge " +
                "use pannava? Yes illa no-nu sollunga.",

            "singlish" =>
                "Oyage reply eka clear naha. General AI " +
                "knowledge use karannada? Yes nathnam no kiyanna.",

            _ =>
                "I’m not sure whether that means yes or no. " +
                "Would you like me to use general AI knowledge?"
        };
    }

    private static string BuildPermissionDeclinedResponse(
        string? language)
    {
        return language?
            .Trim()
            .ToLowerInvariant() switch
        {
            "tamil" =>
                "சரி. பதிவேற்றிய ஆவணத்தில் உள்ள " +
                "தகவல்களை மட்டும் பயன்படுத்துகிறேன்.",

            "tanglish" =>
                "Sari. Uploaded document-la irukkira " +
                "information mattum use panren.",

            "singlish" =>
                "Hari. Uploaded document eke thiyena " +
                "information witharak use karannam.",

            _ =>
                "Okay. I’ll continue using only the " +
                "uploaded document."
        };
    }

    private static string FormatHistory(
        IReadOnlyList<ChatHistoryMessage> history)
    {
        if (history.Count == 0)
        {
            return "(No previous conversation)";
        }

        return string.Join(
            "\n",
            history
                .TakeLast(12)
                .Select(message =>
                    $"{message.Role.ToUpperInvariant()}: " +
                    message.Content));
    }

    private static bool ContainsAny(
        string text,
        params string[] values)
    {
        return values.Any(value =>
            text.Contains(
                value,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsTamilCharacters(
        string text)
    {
        return text.Any(character =>
            character >= '\u0B80' &&
            character <= '\u0BFF');
    }

    private static bool ContainsSinhalaCharacters(
        string text)
    {
        return text.Any(character =>
            character >= '\u0D80' &&
            character <= '\u0DFF');
    }

    private static string CleanAnswer(
        string answer)
    {
        return answer
            .Replace("&#x20;", " ")
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("**", string.Empty)
            .Replace("\\*", "*")
            .Trim();
    }

    private sealed class GroqChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } =
            string.Empty;

        [JsonPropertyName("messages")]
        public List<GroqMessage> Messages
        {
            get;
            set;
        } = [];

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }

        [JsonPropertyName("max_completion_tokens")]
        public int MaxCompletionTokens { get; set; }
    }

    private sealed class GroqMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } =
            string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } =
            string.Empty;
    }

    private sealed class GroqChatResponse
    {
        [JsonPropertyName("choices")]
        public List<GroqChoice> Choices
        {
            get;
            set;
        } = [];
    }

    private sealed class GroqChoice
    {
        [JsonPropertyName("message")]
        public GroqMessage Message { get; set; } =
            new();
    }
}
