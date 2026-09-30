using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Domain.Entities;

public sealed class EmailVerificationOtp
{
    public Guid Id { get; set; } =
        Guid.NewGuid();

    public Guid UserId { get; set; }

    public string CodeHash { get; set; } =
        string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } =
        DateTime.UtcNow;

    public DateTime? UsedAt { get; set; }

    public int FailedAttempts { get; set; }

    public User User { get; set; } =
        null!;
}