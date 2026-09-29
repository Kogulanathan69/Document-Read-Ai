using GazetteAI.Application.Documents.Models;

namespace GazetteAI.Application.Documents.Interfaces;

public interface IChatCompletionService
{
    /*
     * User message மற்றும் previous conversation history-ai
     * analyse செய்து:
     *
     * - search question
     * - language
     * - writing style
     * - follow-up
     * - clarification
     * - translation request
     *
     * ஆகிய தகவல்களை return செய்யும்.
     */
    Task<ConversationAnalysis> AnalyzeConversationAsync(
        string question,
        IReadOnlyList<ChatHistoryMessage> history,
        CancellationToken cancellationToken = default);

    /*
     * Document context, conversation history மற்றும்
     * language analysis பயன்படுத்தி final answer உருவாக்கும்.
     */
    Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<string> contextChunks,
        IReadOnlyList<ChatHistoryMessage> history,
        ConversationAnalysis analysis,
        CancellationToken cancellationToken = default);
}