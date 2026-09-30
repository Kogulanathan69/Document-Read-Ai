using System;
using System.Collections.Generic;
using System.Text;

using System.ComponentModel.DataAnnotations;

namespace GazetteAI.Application.Authentication.DTOs;

public sealed class VerifyEmailRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } =
        string.Empty;

    [Required]
    [RegularExpression(
        @"^\d{6}$",
        ErrorMessage =
            "OTP must contain exactly 6 digits.")]
    public string Code { get; set; } =
        string.Empty;
}
