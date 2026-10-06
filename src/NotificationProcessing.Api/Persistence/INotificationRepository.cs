using NotificationProcessing.Api.Domain;

namespace NotificationProcessing.Api.Persistence;

public interface INotificationRepository
{
    /// <summary>
    /// Atomically stores <paramref name="notification"/> unless a notification with the same fingerprint was
    /// created at or after <paramref name="duplicateWindowStart"/>, in which case that existing one is returned.
    /// Atomicity matters: two retries arriving at the same moment must not both be stored.
    /// </summary>
    Task<(Notification Notification, bool Created)> AddIfNotDuplicateAsync(
        Notification notification, DateTimeOffset duplicateWindowStart, CancellationToken cancellationToken);

    Task<Notification?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Returns the customer's notifications, newest first.</summary>
    Task<IReadOnlyList<Notification>> GetByCustomerAsync(string customerId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns Pending notifications whose delivery time has arrived, in processing order:
    /// priority (High first), then requested delivery time, then arrival order.
    /// </summary>
    Task<IReadOnlyList<Notification>> GetDueForProcessingAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves a notification from Pending to Processing. Returns null if it is no longer Pending
    /// (e.g. another processing run claimed it first), which is what prevents it being sent twice.
    /// </summary>
    Task<Notification?> TryClaimAsync(Guid id, CancellationToken cancellationToken);

    Task UpdateAsync(Notification notification, CancellationToken cancellationToken);
}
