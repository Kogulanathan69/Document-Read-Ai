using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Authentication.Interfaces;

public interface IEmailService
{
    Task SendVerificationOtpAsync(
        string email,
        string fullName,
        string otpCode,
        CancellationToken cancellationToken = default);
}