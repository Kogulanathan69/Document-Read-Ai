using System;
using System.Collections.Generic;
using System.Text;

namespace GazetteAI.Application.Documents.Interfaces;

public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}