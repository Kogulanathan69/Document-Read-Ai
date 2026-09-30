using GazetteAI.Application.Documents.Models;

namespace GazetteAI.Application.Documents.Interfaces;

public interface IChatCompletionService
{
    /*
     * Latest user message மற்றும் previous conversation
     * history-ஐ analyse செய்யும்.
     *
     * Returned ConversationAnalysis contains:
     *
     * - Intent
     * - RequiresDocumentSearch
     * - DirectResponse
     * - SearchQuestion
     * - ResponseLanguage
     * - ResponseStyle
     * - IsFollowUp
     * - IsClarificationRequest
     * - IsTranslationRequest
     * - RequiresExternalKnowledge
     * - IsExternalPermissionResponse
     * - ExternalPermissionGranted
     */
    Task<ConversationAnalysis> AnalyzeConversationAsync(
        string question,
        IReadOnlyList<ChatHistoryMessage> history,
        CancellationToken cancellationToken = default);

    /*
     * Document search தேவைப்படும் messages-க்கு
     * retrieved document context பயன்படுத்தி
     * final answer உருவாக்கும்.
     *
     * Greeting, acknowledgement, casual chat மற்றும்
     * general-knowledge permission flow போன்ற direct-response
     * messages-க்கு இந்த method call செய்யப்படாது.
     */
    Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<string> contextChunks,
        IReadOnlyList<ChatHistoryMessage> history,
        ConversationAnalysis analysis,
        CancellationToken cancellationToken = default);

    /*
     * Creates a current answer strictly from live web-search
     * results after the user has granted permission.
     */
    Task<string> GenerateWebAnswerAsync(
        string question,
        IReadOnlyList<WebSearchResult> searchResults,
        ConversationAnalysis analysis,
        CancellationToken cancellationToken = default);
}
