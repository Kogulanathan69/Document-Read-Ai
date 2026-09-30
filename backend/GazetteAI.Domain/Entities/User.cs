using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FullName { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string Role { get; set; } = "User";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsEmailVerified { get; set; }

    public ICollection<Document> Documents { get; set; } = new List<Document>();

    public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();

    public ICollection<EmailVerificationOtp> EmailVerificationOtps { get; set; } = new List<EmailVerificationOtp>();



}
