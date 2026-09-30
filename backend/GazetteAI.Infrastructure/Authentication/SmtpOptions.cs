using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Infrastructure.Authentication;

public sealed class SmtpOptions
{
    public const string SectionName =
        "Smtp";

    public string Host { get; set; } =
        "smtp.gmail.com";

    public int Port { get; set; } =
        587;

    public string Username { get; set; } =
        string.Empty;

    public string Password { get; set; } =
        string.Empty;

    public string FromEmail { get; set; } =
        string.Empty;

    public string FromName { get; set; } =
        "GazetteAI";

    public int OtpExpiryMinutes { get; set; } =
        10;
}