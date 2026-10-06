using NotificationProcessing.Api.Domain;

namespace NotificationProcessing.Api.Persistence;

/// <summary>
/// Thread-safe in-memory store. A single lock keeps the check-then-act operations (duplicate detection,
/// claiming) atomic; throughput is not a concern at this scale. Data is lost on restart.
/// </summary>
public sealed class InMemoryNotificationRepository : INotificationRepository
{
    private sealed class Entry(Notification notification, long sequence)
    {
        public Notification Notification { get; set; } = notification;
        public long Sequence { get; } = sequence; // insertion order; tie-breaker when timestamps are equal
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _byId = [];
    private readonly Dictionary<string, Guid> _latestByFingerprint = [];
    private long _nextSequence;

    public Task<(Notification Notification, bool Created)> AddIfNotDuplicateAsync(
        Notification notification, DateTimeOffset duplicateWindowStart, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_latestByFingerprint.TryGetValue(notification.Fingerprint, out var existingId))
            {
                var existing = _byId[existingId].Notification;
                if (existing.CreatedAt >= duplicateWindowStart)
                    return Task.FromResult((existing, false));
            }

            _byId.Add(notification.Id, new Entry(notification, _nextSequence++));
            _latestByFingerprint[notification.Fingerprint] = notification.Id;
            return Task.FromResult((notification, true));
        }
    }

    public Task<Notification?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult(_byId.TryGetValue(id, out var entry) ? entry.Notification : null);
    }

    public Task<IReadOnlyList<Notification>> GetByCustomerAsync(string customerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<Notification> result = _byId.Values
                .Where(e => e.Notification.CustomerId == customerId)
                .OrderByDescending(e => e.Notification.CreatedAt)
                .ThenByDescending(e => e.Sequence)
                .Select(e => e.Notification)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<Notification>> GetDueForProcessingAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<Notification> result = _byId.Values
                .Where(e => e.Notification.Status == NotificationStatus.Pending && e.Notification.RequestedDeliveryTime <= now)
                .OrderByDescending(e => e.Notification.Priority)
                .ThenBy(e => e.Notification.RequestedDeliveryTime)
                .ThenBy(e => e.Sequence)
                .Take(maxCount)
                .Select(e => e.Notification)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<Notification?> TryClaimAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var entry) || entry.Notification.Status != NotificationStatus.Pending)
                return Task.FromResult<Notification?>(null);

            entry.Notification = entry.Notification.MarkProcessing();
            return Task.FromResult<Notification?>(entry.Notification);
        }
    }

    public Task UpdateAsync(Notification notification, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(notification.Id, out var entry))
                throw new KeyNotFoundException($"Notification {notification.Id} does not exist.");

            entry.Notification = notification;
            return Task.CompletedTask;
        }
    }
}
