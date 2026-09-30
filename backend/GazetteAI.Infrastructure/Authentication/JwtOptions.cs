namespace GazetteAI.Infrastructure.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } =
        string.Empty;

    public string Issuer { get; set; } =
        "GazetteAI.Api";

    public string Audience { get; set; } =
        "GazetteAI.Web";

    public int ExpiryMinutes { get; set; } = 120;
}