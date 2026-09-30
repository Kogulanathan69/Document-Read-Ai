using System.ComponentModel.DataAnnotations;

namespace GazetteAI.Application.Authentication.DTOs;

public sealed class LoginRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } =
        string.Empty;

    [Required]
    public string Password { get; set; } =
        string.Empty;
}