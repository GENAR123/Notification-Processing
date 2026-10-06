using Microsoft.Extensions.Options;
using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Persistence;
using NotificationProcessing.Api.Providers;

namespace NotificationProcessing.Api.Services;

public sealed record ProcessingSummary(int Attempted, int Sent, int Retrying, int Failed);

/// <summary>
/// Sends due Pending notifications through the matching provider, highest priority first.
/// A provider failure affects only that notification: it is retried on a later run until
/// <see cref="NotificationOptions.MaxSendAttempts"/> is reached, then marked Failed.
/// </summary>
public sealed class NotificationProcessor
{
    private readonly INotificationRepository _repository;
    private readonly IReadOnlyDictionary<NotificationType, INotificationProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<NotificationOptions> _options;
    private readonly ILogger<NotificationProcessor> _logger;

    public NotificationProcessor(
        INotificationRepository repository,
        IEnumerable<INotificationProvider> providers,
        TimeProvider timeProvider,
        IOptions<NotificationOptions> options,
        ILogger<NotificationProcessor> logger)
    {
        _repository = repository;
        _providers = providers.ToDictionary(p => p.Type);
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<ProcessingSummary> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.Value;
        var due = await _repository.GetDueForProcessingAsync(_timeProvider.GetUtcNow(), options.ProcessingBatchSize, cancellationToken);

        int sent = 0, retrying = 0, failed = 0;
        foreach (var candidate in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sequential on purpose: keeps strict priority order and avoids hammering the provider.
            var claimed = await _repository.TryClaimAsync(candidate.Id, cancellationToken);
            if (claimed is null)
                continue; // picked up by a concurrent run

            var result = await DeliverAsync(claimed, options.MaxSendAttempts, cancellationToken);
            switch (result.Status)
            {
                case NotificationStatus.Sent: sent++; break;
                case NotificationStatus.Pending: retrying++; break;
                case NotificationStatus.Failed: failed++; break;
            }
        }

        return new ProcessingSummary(sent + retrying + failed, sent, retrying, failed);
    }

    private async Task<Notification> DeliverAsync(Notification notification, int maxAttempts, CancellationToken cancellationToken)
    {
        NotificationProviderResult result;
        if (!_providers.TryGetValue(notification.Type, out var provider))
        {
            result = NotificationProviderResult.PermanentFailure($"No provider is registered for {notification.Type} notifications.");
        }
        else
        {
            try
            {
                result = await provider.SendAsync(notification, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down / request aborted: return it to the queue rather than leave it stuck in Processing.
                await _repository.UpdateAsync(notification.ReleaseClaim(), CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                // Unexpected provider exceptions (timeouts, outages) are treated as transient.
                _logger.LogWarning(ex, "Provider threw while sending notification {NotificationId}.", notification.Id);
                result = NotificationProviderResult.TransientFailure(ex.Message);
            }
        }

        var now = _timeProvider.GetUtcNow();
        var updated = result.Succeeded
            ? notification.MarkSent(now)
            : notification.MarkAttemptFailed(now, result.Error ?? "Unknown provider error.",
                canRetry: !result.IsPermanent && notification.AttemptCount + 1 < maxAttempts);

        // The send has already happened, so record the outcome even if cancellation was requested meanwhile.
        await _repository.UpdateAsync(updated, CancellationToken.None);

        if (updated.Status == NotificationStatus.Sent)
            _logger.LogInformation("Notification {NotificationId} sent.", updated.Id);
        else
            _logger.LogWarning("Notification {NotificationId} attempt {Attempt} failed ({Error}); status is now {Status}.",
                updated.Id, updated.AttemptCount, updated.LastError, updated.Status);

        return updated;
    }
}
