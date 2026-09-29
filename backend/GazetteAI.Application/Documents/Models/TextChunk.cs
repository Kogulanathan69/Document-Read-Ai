using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Documents.Models
{
    public sealed record TextChunk(
    int ChunkIndex,
    int PageNumber,
    string Content,
    int TokenCount
    );
}
