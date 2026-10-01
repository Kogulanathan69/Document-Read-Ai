using GazetteAI.Application.Documents.BackgroundProcessing;

namespace GazetteAI.Api.BackgroundServices;

public sealed class DocumentProcessingWorker : BackgroundService
{
    private readonly IDocumentProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DocumentProcessingWorker> _logger;

    public DocumentProcessingWorker(
        IDocumentProcessingQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<DocumentProcessingWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Document processing worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job =
                    await _queue.DequeueAsync(
                        stoppingToken);

                _logger.LogInformation(
                    "Processing document {DocumentId}",
                    job.DocumentId);

                using var scope =
                    _scopeFactory.CreateScope();

                var processingService =
                    scope.ServiceProvider
                        .GetRequiredService<IDocumentProcessingService>();

                await processingService.ProcessAsync(
                    job,
                    stoppingToken);

                _logger.LogInformation(
                    "Document {DocumentId} processing completed.",
                    job.DocumentId);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Background document processing failed.");
            }
        }

        _logger.LogInformation(
            "Document processing worker stopped.");
    }
}