using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Domain.Entities;

public class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId { get; set; }

    public Guid UserId { get; set; }

    public int ChunkIndex { get; set; }

    public int PageNumber { get; set; }

    public string Content { get; set; } = string.Empty;

    public float[] Embedding { get; set; } = Array.Empty<float>();

    public int TokenCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;

    public Document Document { get; set; } = null!;
}
