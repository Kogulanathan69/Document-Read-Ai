namespace GazetteAI.Infrastructure.AI;

public sealed class GroqOptions
{
    public const string SectionName = "Groq";

    public string BaseUrl { get; set; } =
        "https://api.groq.com/openai/v1/";

    public string ApiKey { get; set; } =
        string.Empty;

    public string ChatModel { get; set; } =
        "qwen/qwen3.8-27b";
}