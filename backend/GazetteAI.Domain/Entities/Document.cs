using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Domain.Entities;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string StoragePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public int? TotalPages { get; set; }

    public string Status { get; set; } = "Pending";

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;

    public ICollection<DocumentChunk> Chunks { get; set; } = new List<DocumentChunk>();

    public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
}
