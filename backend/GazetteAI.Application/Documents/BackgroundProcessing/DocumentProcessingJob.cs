namespace GazetteAI.Application.Documents.BackgroundProcessing;

public sealed record DocumentProcessingJob(
    Guid DocumentId,
    Guid UserId,
    string StoragePath);