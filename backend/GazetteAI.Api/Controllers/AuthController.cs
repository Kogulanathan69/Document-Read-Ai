using GazetteAI.Application.Authentication.DTOs;
using GazetteAI.Application.Authentication.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GazetteAI.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response =
                await _authService.RegisterAsync(
                    request,
                    cancellationToken);

            return Ok(response);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new
            {
                message = exception.Message
            });
        }
    }

    [AllowAnonymous]
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyEmailRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response =
                await _authService.VerifyEmailAsync(
                    request,
                    cancellationToken);

            if (response is null)
            {
                return BadRequest(new
                {
                    message =
                        "Invalid verification code."
                });
            }

            return Ok(new
            {
                message =
                    "Email verified successfully.",

                userId = response.UserId,
                fullName = response.FullName,
                email = response.Email,
                role = response.Role,
                token = response.Token,
                expiresAt = response.ExpiresAt
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new
            {
                message = exception.Message
            });
        }
    }

    [AllowAnonymous]
    [HttpPost("resend-otp")]
    public async Task<IActionResult> ResendOtp(
        [FromBody] ResendOtpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _authService.ResendVerificationOtpAsync(
                request,
                cancellationToken);

            /*
             * Account enumeration prevent panna,
             * email exist aagutha illaya endru response-la
             * reveal panna koodathu.
             */
            return Ok(new
            {
                message =
                    "If the account exists and is not verified, " +
                    "a new OTP has been sent."
            });
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new
            {
                message = exception.Message
            });
        }
    }

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response =
                await _authService.LoginAsync(
                    request,
                    cancellationToken);

            if (response is null)
            {
                return Unauthorized(new
                {
                    message =
                        "Invalid email or password."
                });
            }

            return Ok(new
            {
                message = "Login successful.",
                userId = response.UserId,
                fullName = response.FullName,
                email = response.Email,
                role = response.Role,
                token = response.Token,
                expiresAt = response.ExpiresAt
            });
        }
        catch (InvalidOperationException exception)
        {
            /*
             * Correct password but email verification
             * complete aagala endraal 403 return aagum.
             */
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new
                {
                    message = exception.Message
                });
        }
    }
}