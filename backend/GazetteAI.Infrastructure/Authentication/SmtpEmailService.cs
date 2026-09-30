using GazetteAI.Application.Authentication.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GazetteAI.Infrastructure.Authentication;

public sealed class SmtpEmailService : IEmailService
{
    private readonly SmtpOptions _options;

    public SmtpEmailService(
        IOptions<SmtpOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendVerificationOtpAsync(
        string email,
        string fullName,
        string otpCode,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions();

        var message = new MimeMessage();

        message.From.Add(
            new MailboxAddress(
                _options.FromName,
                _options.FromEmail));

        message.To.Add(
            MailboxAddress.Parse(email));

        message.Subject =
            "GazetteAI email verification code";

        var safeName =
            string.IsNullOrWhiteSpace(fullName)
                ? "User"
                : fullName.Trim();

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = $"""
                <div style="
                    max-width:520px;
                    margin:0 auto;
                    padding:28px;
                    font-family:Arial,sans-serif;
                    background:#f7f7fb;
                    border-radius:16px;">

                    <h2 style="color:#342e5c;">
                        GazetteAI Email Verification
                    </h2>

                    <p>Hello {safeName},</p>

                    <p>
                        Use the following verification code
                        to activate your GazetteAI account:
                    </p>

                    <div style="
                        margin:24px 0;
                        padding:18px;
                        text-align:center;
                        font-size:32px;
                        font-weight:bold;
                        letter-spacing:8px;
                        color:#ffffff;
                        background:#6b4eff;
                        border-radius:12px;">
                        {otpCode}
                    </div>

                    <p>
                        This code expires in
                        {_options.OtpExpiryMinutes} minutes.
                    </p>

                    <p>
                        If you did not create this account,
                        you can safely ignore this email.
                    </p>
                </div>
                """,

            TextBody = $"""
                Hello {safeName},

                Your GazetteAI email verification code is:

                {otpCode}

                This code expires in
                {_options.OtpExpiryMinutes} minutes.

                If you did not create this account,
                ignore this email.
                """
        };

        message.Body = bodyBuilder.ToMessageBody();

        using var smtpClient =
            new SmtpClient();

        await smtpClient.ConnectAsync(
            _options.Host,
            _options.Port,
            SecureSocketOptions.StartTls,
            cancellationToken);

        await smtpClient.AuthenticateAsync(
            _options.Username,
            _options.Password,
            cancellationToken);

        await smtpClient.SendAsync(
            message,
            cancellationToken);

        await smtpClient.DisconnectAsync(
            true,
            cancellationToken);
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(
                _options.Host))
        {
            throw new InvalidOperationException(
                "SMTP host is missing.");
        }

        if (string.IsNullOrWhiteSpace(
                _options.Username))
        {
            throw new InvalidOperationException(
                "SMTP username is missing.");
        }

        if (string.IsNullOrWhiteSpace(
                _options.Password))
        {
            throw new InvalidOperationException(
                "SMTP password is missing.");
        }

        if (string.IsNullOrWhiteSpace(
                _options.FromEmail))
        {
            throw new InvalidOperationException(
                "SMTP sender email is missing.");
        }
    }
}