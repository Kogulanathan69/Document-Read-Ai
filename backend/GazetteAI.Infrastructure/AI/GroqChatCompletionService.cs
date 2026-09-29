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
              "isFollowUp": false
            }

            Rules:

            1. intent must be exactly one of:
               DocumentQuestion, DocumentFollowUp,
               Clarification, Translation, Acknowledgement,
               Greeting or OutOfScope.

            2. requiresDocumentSearch must be false only for
               Greeting and Acknowledgement. It must be true
               for document questions, follow-ups,
               clarifications and translations.

            3. For Greeting or Acknowledgement, put a short,
               friendly reply in directResponse. For every
               other intent, directResponse must be empty.

            4. searchQuestion must be a complete standalone
               question used to search the uploaded document.

            5. Use conversation history only to understand
               follow-up references.

            6. Messages such as "what is its scope?",
               "what does it do?" and "mokad karanne?" are
               document follow-ups when history contains a
               document topic. Rewrite them as standalone
               document questions.

            7. Messages such as "okay", "hari", "hri okay",
               "thanks" and "got it" are acknowledgements,
               not document questions.

            8. Never answer document questions during analysis.

            9. Detect the language requested by the latest user.

            10. Tanglish means spoken Tamil written using
               Latin letters.

            11. Singlish means spoken Sinhala written using
               Latin letters.

            12. Do not mix Tanglish and Singlish.

            13. responseStyle must be:
               standard, simple or detailed.

            14. Return JSON only.
               Do not use Markdown.
            """;

        var userPrompt = $"""
            CONVERSATION HISTORY:

            {formattedHistory}

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
            return BuildNotFoundMessage(
                responseLanguage);
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
            return BuildNotFoundMessage(
                responseLanguage);
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
            if (ContainsAny(
                    normalized,
                    "puriyala",
                    "purila",
                    "puriyavillai",
                    "simple ah sollu",
                    "simple-a sollu",
                    "easy ah sollu",
                    "thelivaga sollu"))
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
                "singlish",
                "singlish walin",
                "sinhala walin",
                "sinhalen"))
        {
            return "Singlish";
        }

        if (ContainsAny(
                normalized,
                "tanglish",
                "tamil la",
                "tamil-la",
                "thamizh la",
                "thamizh-la"))
        {
            return "Tanglish";
        }

        if (ContainsAny(
                normalized,
                "franÃ§ais",
                "french",
                "en franÃ§ais"))
        {
            return "French";
        }

        if (ContainsAny(
                normalized,
                "hindi",
                "à¤¹à¤¿à¤‚à¤¦à¥€"))
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
                "thelivaga sollu"))
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
                IsAcknowledgement(normalized))
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
        return ContainsAny(
            text,
            "singlish walin kiyanna",
            "singlish walin",
            "sinhala walin kiyanna",
            "sinhala walin",
            "tamil la sollu",
            "tamil-la sollu",
            "tanglish la sollu",
            "à®¤à®®à®¿à®´à®¿à®²à¯ à®šà¯Šà®²à¯à®²à¯à®™à¯à®•à®³à¯",
            "à®¤à®®à®¿à®´à®¿à®²à¯ à®šà¯Šà®²à¯à®²à¯",
            "à®šà®¿à®™à¯à®•à®³à®¤à¯à®¤à®¿à®²à¯ à®šà¯Šà®²à¯à®²à¯à®™à¯à®•à®³à¯",
            "in english",
            "english la sollu",
            "english walin",
            "answer in tamil",
            "answer in sinhala",
            "answer in english",
            "answer in french",
            "rÃ©pondez en franÃ§ais",
            "en franÃ§ais",
            "à¤‰à¤¤à¥à¤¤à¤° à¤¹à¤¿à¤‚à¤¦à¥€ à¤®à¥‡à¤‚",
            "hindi mein",
            "hindi me");
    }

    private static bool IsClarificationCommand(
        string text)
    {
        return ContainsAny(
            text,
            "puriyala",
            "purila",
            "puriyavillai",
            "simple ah sollu",
            "simple-a sollu",
            "easy ah sollu",
            "thelivaga sollu",
            "à®¤à¯†à®³à®¿à®µà®¾à®• à®šà¯Šà®²à¯à®²à¯à®™à¯à®•à®³à¯",
            "à®Žà®³à®¿à®®à¯ˆà®¯à®¾à®• à®šà¯Šà®²à¯à®²à¯à®™à¯à®•à®³à¯",
            "à®ªà¯à®°à®¿à®¯à®µà®¿à®²à¯à®²à¯ˆ",
            "therenne naha",
            "à¶­à·šà¶»à·™à¶±à·Šà¶±à·š à¶±à·à·„à·",
            "simple walin kiyanna",
            "explain clearly",
            "explain simply",
            "i don't understand",
            "i do not understand");
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
            "à®µà®£à®•à¯à®•à®®à¯" or
            "ayubowan" or
            "à¶†à¶ºà·”à¶¶à·à·€à¶±à·Š";
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
            "à®šà®°à®¿" or
            "à®šà®°à®¿ à®¨à®©à¯à®±à®¿" or
            "à®¨à®©à¯à®±à®¿" or
            "à·„à¶»à·’" or
            "à·ƒà·Šà¶­à·”à¶­à·’à¶ºà·’";
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
                "à®‡à®¨à¯à®¤ à®†à®µà®£à®¤à¯à®¤à®¿à®²à¯ à®ªà®¤à®¿à®²à¯ à®•à®¾à®£à®ªà¯à®ªà®Ÿà®µà®¿à®²à¯à®²à¯ˆ.",

            "tanglish" =>
                "Indha document-la badhil kidaikkala.",

            "sinhala" =>
                "à¶¸à·™à¶¸ à¶½à·šà¶›à¶±à¶ºà·š à¶´à·’à·…à·’à¶­à·”à¶» à·ƒà¶³à·„à¶±à·Š à¶šà¶» à¶±à·à·„à·.",

            "singlish" =>
                "Me document eke uththaraya sandahan wela naha.",

            "french" =>
                "La rÃ©ponse ne figure pas dans ce document.",

            "hindi" =>
                "à¤‡à¤¸ à¤¦à¤¸à¥à¤¤à¤¾à¤µà¥‡à¤œà¤¼ à¤®à¥‡à¤‚ à¤‰à¤¤à¥à¤¤à¤° à¤¨à¤¹à¥€à¤‚ à¤®à¤¿à¤²à¤¾à¥¤",

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
            "OutOfScope"
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

        var isDirectIntent =
            analysis.Intent == "Greeting" ||
            analysis.Intent == "Acknowledgement";

        analysis.RequiresDocumentSearch =
            !isDirectIntent;

        if (isDirectIntent)
        {
            if (string.IsNullOrWhiteSpace(
                    analysis.DirectResponse))
            {
                analysis.DirectResponse =
                    BuildDirectResponse(
                        question,
                        analysis.Intent == "Greeting");
            }

            analysis.SearchQuestion = string.Empty;
            analysis.IsFollowUp = false;
            analysis.IsClarificationRequest = false;
            analysis.IsTranslationRequest = false;
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