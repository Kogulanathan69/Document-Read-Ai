using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Documents.Models;

public sealed class ConversationAnalysis
{
    /*
     * Groq service பயன்படுத்தும் primary properties
     */

    public string SearchQuestion { get; set; } =
        string.Empty;

    public string ResponseLanguage { get; set; } =
        "English";

    public string ResponseStyle { get; set; } =
        "standard";

    public bool IsClarificationRequest { get; set; }

    /*
     * QuestionsController compatibility properties
     */

    public string DetectedLanguage
    {
        get => ResponseLanguage;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ResponseLanguage = value;
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
                ResponseStyle = value;
            }
        }
    }

    public bool IsClarification
    {
        get => IsClarificationRequest;
        set => IsClarificationRequest = value;
    }

    public bool IsTranslationRequest { get; set; }

    public bool IsFollowUp { get; set; }

    public string ResponseInstruction { get; set; } =
        string.Empty;
}
