namespace NotificationProcessing.Api.Domain;

/// <summary>
/// A stored notification. Immutable: state changes produce a new instance that is then persisted,
/// so a caller holding a reference never observes a half-applied update.
/// </summary>
public sealed record Notification
{
    public required Guid Id { get; init; }
    public required string CustomerId { get; init; }
    public required NotificationType Type { get; init; }
    public required string Recipient { get; init; }
    public string? Subject { get; init; }
    public required string Message { get; init; }
    public required NotificationPriority Priority { get; init; }
    public required DateTimeOffset RequestedDeliveryTime { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Hash of the request content, used to detect retried (duplicate) submissions.</summary>
    public required string Fingerprint { get; init; }

    public NotificationStatus Status { get; init; } = NotificationStatus.Pending;
    public int AttemptCount { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public string? LastError { get; init; }

    public Notification MarkProcessing() => this with { Status = NotificationStatus.Processing };

    /// <summary>Returns a claimed notification to the queue without counting an attempt (e.g. on shutdown).</summary>
    public Notification ReleaseClaim() => this with { Status = NotificationStatus.Pending };

    public Notification MarkSent(DateTimeOffset at) => this with
    {
        Status = NotificationStatus.Sent,
        AttemptCount = AttemptCount + 1,
        LastAttemptAt = at,
        SentAt = at,
        LastError = null
    };

    /// <param name="canRetry">True puts the notification back to Pending; false marks it permanently Failed.</param>
    public Notification MarkAttemptFailed(DateTimeOffset at, string error, bool canRetry) => this with
    {
        Status = canRetry ? NotificationStatus.Pending : NotificationStatus.Failed,
        AttemptCount = AttemptCount + 1,
        LastAttemptAt = at,
        LastError = error
    };
}
