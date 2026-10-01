using System.Threading.Channels;
using GazetteAI.Application.Documents.BackgroundProcessing;

namespace GazetteAI.Infrastructure.Documents;

public sealed class DocumentProcessingQueue
    : IDocumentProcessingQueue
{
    private readonly Channel<DocumentProcessingJob> _queue;

    public DocumentProcessingQueue()
    {
        var options =
            new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            };

        _queue =
            Channel.CreateBounded<DocumentProcessingJob>(
                options);
    }

    public async ValueTask QueueAsync(
        DocumentProcessingJob job,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await _queue.Writer.WriteAsync(
            job,
            cancellationToken);
    }

    public async ValueTask<DocumentProcessingJob> DequeueAsync(
        CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(
            cancellationToken);
    }
}