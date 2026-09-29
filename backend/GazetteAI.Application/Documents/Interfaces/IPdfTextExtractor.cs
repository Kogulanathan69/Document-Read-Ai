using System;
using System.Collections.Generic;
using System.Text;
using GazetteAI.Application.Documents.Models;

namespace GazetteAI.Application.Documents.Interfaces;

public interface IPdfTextExtractor
{
    Task<IReadOnlyList<ExtractedPage>> ExtractAsync(
        Stream pdfStream,
        CancellationToken cancellationToken = default
    );
}