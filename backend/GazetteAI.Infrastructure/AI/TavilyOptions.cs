namespace GazetteAI.Infrastructure.AI;

public sealed class TavilyOptions
{
    public const string SectionName = "Tavily";

    public string BaseUrl { get; set; } =
        "https://api.tavily.com/";

    public string ApiKey { get; set; } = string.Empty;

    public int MaxResults { get; set; } = 5;
}
