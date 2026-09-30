using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Authentication.DTOs;

public sealed class RegisterResponse
{
    public Guid UserId { get; set; }

    public string Email { get; set; } =
        string.Empty;

    public string Message { get; set; } =
        string.Empty;

    public DateTime OtpExpiresAt { get; set; }
}
