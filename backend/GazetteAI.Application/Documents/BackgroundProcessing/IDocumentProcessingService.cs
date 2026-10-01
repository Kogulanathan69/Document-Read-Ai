namespace GazetteAI.Application.Documents.BackgroundProcessing;

public interface IDocumentProcessingService
{
    Task ProcessAsync(
        DocumentProcessingJob job,
        CancellationToken cancellationToken);
}