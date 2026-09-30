using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Documents.Models;

public sealed class ConversationAnalysis
{
    /*
     * User message எந்த வகையைச் சேர்ந்தது என்பதை
     * குறிப்பிடும்.
     *
     * Supported values:
     *
     * DocumentQuestion
     * DocumentFollowUp
     * Clarification
     * Translation
     * Acknowledgement
     * Greeting
     * CasualConversation
     * GeneralQuestion
     * GeneralPermissionGranted
     * GeneralPermissionDenied
     */
    public string Intent { get; set; } =
        "DocumentQuestion";

    /*
     * true:
     * Ollama embedding மற்றும் document chunk
     * search செய்ய வேண்டும்.
     *
     * false:
     * Greeting அல்லது acknowledgement போன்ற
     * messages-க்கு document search தேவையில்லை.
     */
    public bool RequiresDocumentSearch { get; set; } =
        true;

    /*
     * Document search தேவையில்லாத messages-க்கு
     * நேரடியாக return செய்ய வேண்டிய friendly response.
     */
    public string DirectResponse { get; set; } =
        string.Empty;

    /*
     * Embedding search-க்கு பயன்படுத்தப்படும்
     * standalone document question.
     */
    public string SearchQuestion { get; set; } =
        string.Empty;

    /*
     * Final answer எழுத வேண்டிய மொழி.
     *
     * Examples:
     * English
     * Tamil
     * Tanglish
     * Sinhala
     * Singlish
     * French
     * Hindi
     */
    public string ResponseLanguage { get; set; } =
        "English";

    /*
     * Final answer style.
     *
     * Supported values:
     * standard
     * simple
     * detailed
     */
    public string ResponseStyle { get; set; } =
        "standard";

    public bool IsFollowUp { get; set; }

    public bool IsClarificationRequest { get; set; }

    public bool IsTranslationRequest { get; set; }

    /*
     * GeneralQuestion means that the message is not asking
     * about the uploaded document. The assistant first asks
     * permission before using general model knowledge.
     */
    public bool RequiresExternalKnowledge { get; set; }

    public bool IsExternalPermissionResponse { get; set; }

    public bool ExternalPermissionGranted { get; set; }

    /*
     * Permission was granted for this earlier question.
     * The API uses it for live web search; it is never
     * treated as document content.
     */
    public string ExternalQuestion { get; set; } =
        string.Empty;

    /*
     * Optional additional instruction generated
     * during conversation analysis.
     */
    public string ResponseInstruction { get; set; } =
        string.Empty;

    /*
     * QuestionsController compatibility properties.
     */

    public string DetectedLanguage
    {
        get => ResponseLanguage;

        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ResponseLanguage = value.Trim();
            }
        }
    }

    public string WritingStyle
    {
        get => ResponseStyle;

        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ResponseStyle =
                    value.Trim().ToLowerInvariant();
            }
        }
    }

    public bool IsClarification
    {
        get => IsClarificationRequest;
        set => IsClarificationRequest = value;
    }
}
