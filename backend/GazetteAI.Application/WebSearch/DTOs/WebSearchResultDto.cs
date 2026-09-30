namespace GazetteAI.Application.WebSearch.DTOs;

public class WebSearchResultDto
{
    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public double Score { get; set; }
}
