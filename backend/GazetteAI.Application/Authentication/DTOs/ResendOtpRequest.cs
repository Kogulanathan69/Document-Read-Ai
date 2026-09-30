using System.ComponentModel.DataAnnotations;

namespace GazetteAI.Application.Authentication.DTOs;

public sealed class ResendOtpRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } =
        string.Empty;
}