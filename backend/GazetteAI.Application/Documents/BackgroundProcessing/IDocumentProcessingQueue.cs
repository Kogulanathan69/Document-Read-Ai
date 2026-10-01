namespace GazetteAI.Application.Documents.BackgroundProcessing;

public interface IDocumentProcessingQueue
{
    ValueTask QueueAsync(
        DocumentProcessingJob job,
        CancellationToken cancellationToken = default);

    ValueTask<DocumentProcessingJob> DequeueAsync(
        CancellationToken cancellationToken);
}