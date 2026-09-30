using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GazetteAI.Application.Authentication.DTOs;
using GazetteAI.Application.Authentication.Interfaces;
using GazetteAI.Domain.Entities;
using GazetteAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GazetteAI.Infrastructure.Authentication;

public sealed class AuthService : IAuthService
{
    private const int MaximumOtpAttempts = 5;
    private const int ResendDelaySeconds = 60;

    private readonly AppDbContext _dbContext;
    private readonly IEmailService _emailService;
    private readonly JwtOptions _jwtOptions;
    private readonly SmtpOptions _smtpOptions;

    public AuthService(
        AppDbContext dbContext,
        IEmailService emailService,
        IOptions<JwtOptions> jwtOptions,
        IOptions<SmtpOptions> smtpOptions)
    {
        _dbContext = dbContext;
        _emailService = emailService;
        _jwtOptions = jwtOptions.Value;
        _smtpOptions = smtpOptions.Value;
    }

    public async Task<RegisterResponse> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail =
            NormalizeEmail(request.Email);

        var existingUser =
            await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    user =>
                        user.Email == normalizedEmail,
                    cancellationToken);

        if (existingUser is not null)
        {
            if (!existingUser.IsEmailVerified)
            {
                throw new InvalidOperationException(
                    "This account is awaiting email verification. " +
                    "Use resend OTP.");
            }

            throw new InvalidOperationException(
                "An account already exists with this email.");
        }

        var otpCode = GenerateOtpCode();
        var now = DateTime.UtcNow;
        var expiresAt =
            now.AddMinutes(
                _smtpOptions.OtpExpiryMinutes);

        var user = new User
        {
            Id = Guid.NewGuid(),
            FullName = request.FullName.Trim(),
            Email = normalizedEmail,

            PasswordHash =
                BCrypt.Net.BCrypt.HashPassword(
                    request.Password),

            Role = "User",
            IsEmailVerified = false,
            CreatedAt = now
        };

        var otp = new EmailVerificationOtp
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CodeHash = HashOtp(otpCode),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            FailedAttempts = 0
        };

        user.EmailVerificationOtps.Add(otp);

        _dbContext.Users.Add(user);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await _emailService.SendVerificationOtpAsync(
            user.Email,
            user.FullName,
            otpCode,
            cancellationToken);

        return new RegisterResponse
        {
            UserId = user.Id,
            Email = user.Email,

            Message =
                "Registration successful. " +
                "Check your email for the verification code.",

            OtpExpiresAt = expiresAt
        };
    }

    public async Task<AuthResponse?> VerifyEmailAsync(
        VerifyEmailRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail =
            NormalizeEmail(request.Email);

        var user =
            await _dbContext.Users
                .FirstOrDefaultAsync(
                    item =>
                        item.Email == normalizedEmail,
                    cancellationToken);

        if (user is null)
        {
            return null;
        }

        if (user.IsEmailVerified)
        {
            return CreateAuthResponse(user);
        }

        var otp =
            await _dbContext.EmailVerificationOtps
                .Where(item =>
                    item.UserId == user.Id &&
                    item.UsedAt == null)
                .OrderByDescending(item =>
                    item.CreatedAt)
                .FirstOrDefaultAsync(
                    cancellationToken);

        if (otp is null)
        {
            throw new InvalidOperationException(
                "No active verification code was found. " +
                "Request a new OTP.");
        }

        var now = DateTime.UtcNow;

        if (otp.ExpiresAt <= now)
        {
            otp.UsedAt = now;

            await _dbContext.SaveChangesAsync(
                cancellationToken);

            throw new InvalidOperationException(
                "The verification code has expired. " +
                "Request a new OTP.");
        }

        if (otp.FailedAttempts >= MaximumOtpAttempts)
        {
            otp.UsedAt = now;

            await _dbContext.SaveChangesAsync(
                cancellationToken);

            throw new InvalidOperationException(
                "Too many incorrect attempts. " +
                "Request a new OTP.");
        }

        var otpIsValid =
            VerifyOtp(
                request.Code.Trim(),
                otp.CodeHash);

        if (!otpIsValid)
        {
            otp.FailedAttempts++;

            if (otp.FailedAttempts >=
                MaximumOtpAttempts)
            {
                otp.UsedAt = now;
            }

            await _dbContext.SaveChangesAsync(
                cancellationToken);

            return null;
        }

        user.IsEmailVerified = true;
        otp.UsedAt = now;

        var otherActiveOtps =
            await _dbContext.EmailVerificationOtps
                .Where(item =>
                    item.UserId == user.Id &&
                    item.Id != otp.Id &&
                    item.UsedAt == null)
                .ToListAsync(cancellationToken);

        foreach (var activeOtp in otherActiveOtps)
        {
            activeOtp.UsedAt = now;
        }

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        return CreateAuthResponse(user);
    }

    public async Task<bool> ResendVerificationOtpAsync(
        ResendOtpRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail =
            NormalizeEmail(request.Email);

        var user =
            await _dbContext.Users
                .FirstOrDefaultAsync(
                    item =>
                        item.Email == normalizedEmail,
                    cancellationToken);

        /*
         * Returning true for an unknown email prevents
         * account-enumeration attacks.
         */
        if (user is null)
        {
            return true;
        }

        if (user.IsEmailVerified)
        {
            throw new InvalidOperationException(
                "This email address is already verified.");
        }

        var latestOtp =
            await _dbContext.EmailVerificationOtps
                .Where(item =>
                    item.UserId == user.Id)
                .OrderByDescending(item =>
                    item.CreatedAt)
                .FirstOrDefaultAsync(
                    cancellationToken);

        var now = DateTime.UtcNow;

        if (latestOtp is not null &&
            latestOtp.CreatedAt >
            now.AddSeconds(-ResendDelaySeconds))
        {
            throw new InvalidOperationException(
                "Please wait before requesting " +
                "another verification code.");
        }

        var activeOtps =
            await _dbContext.EmailVerificationOtps
                .Where(item =>
                    item.UserId == user.Id &&
                    item.UsedAt == null)
                .ToListAsync(cancellationToken);

        foreach (var activeOtp in activeOtps)
        {
            activeOtp.UsedAt = now;
        }

        var otpCode = GenerateOtpCode();

        var otp = new EmailVerificationOtp
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CodeHash = HashOtp(otpCode),
            CreatedAt = now,

            ExpiresAt =
                now.AddMinutes(
                    _smtpOptions.OtpExpiryMinutes),

            FailedAttempts = 0
        };

        _dbContext.EmailVerificationOtps.Add(otp);

        await _dbContext.SaveChangesAsync(
            cancellationToken);

        await _emailService.SendVerificationOtpAsync(
            user.Email,
            user.FullName,
            otpCode,
            cancellationToken);

        return true;
    }

    public async Task<AuthResponse?> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail =
            NormalizeEmail(request.Email);

        var user =
            await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item =>
                        item.Email == normalizedEmail,
                    cancellationToken);

        if (user is null)
        {
            return null;
        }

        if (!VerifyPassword(
                request.Password,
                user.PasswordHash))
        {
            return null;
        }

        if (!user.IsEmailVerified)
        {
            throw new InvalidOperationException(
                "Verify your email before logging in.");
        }

        return CreateAuthResponse(user);
    }

    private AuthResponse CreateAuthResponse(
        User user)
    {
        ValidateJwtOptions();

        var now = DateTime.UtcNow;

        var expiresAt =
            now.AddMinutes(
                _jwtOptions.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(
                JwtRegisteredClaimNames.Sub,
                user.Id.ToString()),

            new(
                ClaimTypes.NameIdentifier,
                user.Id.ToString()),

            new(
                ClaimTypes.Name,
                user.FullName),

            new(
                ClaimTypes.Email,
                user.Email),

            new(
                ClaimTypes.Role,
                user.Role),

            new(
                JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString())
        };

        var signingKey =
            new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(
                    _jwtOptions.Key));

        var credentials =
            new SigningCredentials(
                signingKey,
                SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: credentials);

        var tokenValue =
            new JwtSecurityTokenHandler()
                .WriteToken(token);

        return new AuthResponse
        {
            UserId = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Role = user.Role,
            Token = tokenValue,
            ExpiresAt = expiresAt
        };
    }

    private void ValidateJwtOptions()
    {
        if (string.IsNullOrWhiteSpace(
                _jwtOptions.Key))
        {
            throw new InvalidOperationException(
                "JWT key is missing.");
        }

        if (Encoding.UTF8.GetByteCount(
                _jwtOptions.Key) < 32)
        {
            throw new InvalidOperationException(
                "JWT key must contain at least 32 bytes.");
        }

        if (string.IsNullOrWhiteSpace(
                _jwtOptions.Issuer))
        {
            throw new InvalidOperationException(
                "JWT issuer is missing.");
        }

        if (string.IsNullOrWhiteSpace(
                _jwtOptions.Audience))
        {
            throw new InvalidOperationException(
                "JWT audience is missing.");
        }

        if (_jwtOptions.ExpiryMinutes <= 0)
        {
            throw new InvalidOperationException(
                "JWT expiry must be greater than zero.");
        }
    }

    private static string NormalizeEmail(
        string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static string GenerateOtpCode()
    {
        var value =
            RandomNumberGenerator.GetInt32(
                100000,
                1000000);

        return value.ToString();
    }

    private static string HashOtp(
        string otpCode)
    {
        var bytes =
            Encoding.UTF8.GetBytes(
                otpCode);

        var hash =
            SHA256.HashData(bytes);

        return Convert.ToHexString(hash);
    }

    private static bool VerifyOtp(
        string otpCode,
        string expectedHash)
    {
        try
        {
            var suppliedHash =
                Convert.FromHexString(
                    HashOtp(otpCode));

            var storedHash =
                Convert.FromHexString(
                    expectedHash);

            return CryptographicOperations
                .FixedTimeEquals(
                    suppliedHash,
                    storedHash);
        }
        catch
        {
            return false;
        }
    }

    private static bool VerifyPassword(
        string password,
        string passwordHash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(
                password,
                passwordHash);
        }
        catch
        {
            return false;
        }
    }
}