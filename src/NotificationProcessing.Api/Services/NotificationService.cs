using Microsoft.Extensions.Options;
using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Persistence;

namespace NotificationProcessing.Api.Services;

public sealed record CreateNotificationResult(Notification Notification, bool IsDuplicate);

public sealed class NotificationService(
    INotificationRepository repository,
    TimeProvider timeProvider,
    IOptions<NotificationOptions> options,
    ILogger<NotificationService> logger)
{
    /// <summary>
    /// Stores a validated notification as Pending. If the identical request was already accepted within the
    /// duplicate window, nothing new is stored and the original notification is returned instead.
    /// </summary>
    public async Task<CreateNotificationResult> CreateAsync(NewNotification request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            Type = request.Type,
            Recipient = request.Recipient,
            Subject = request.Subject,
            Message = request.Message,
            Priority = request.Priority,
            RequestedDeliveryTime = request.RequestedDeliveryTime ?? now,
            CreatedAt = now,
            Fingerprint = NotificationFingerprint.Compute(request)
        };

        var (stored, created) = await repository.AddIfNotDuplicateAsync(
            notification, now - options.Value.DuplicateWindow, cancellationToken);

        if (created)
            logger.LogInformation("Accepted notification {NotificationId} for customer {CustomerId}.", stored.Id, stored.CustomerId);
        else
            logger.LogInformation("Duplicate request for customer {CustomerId}; returning existing notification {NotificationId}.",
                stored.CustomerId, stored.Id);

        return new CreateNotificationResult(stored, IsDuplicate: !created);
    }

    public Task<Notification?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        repository.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<Notification>> GetByCustomerAsync(string customerId, CancellationToken cancellationToken) =>
        repository.GetByCustomerAsync(customerId.Trim(), cancellationToken);
}
