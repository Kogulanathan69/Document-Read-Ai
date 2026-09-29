using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Domain.Entities;

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }

    public Guid UserId { get; set; }

    public Guid? DocumentId { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;

    public Document? Document { get; set; }
}
