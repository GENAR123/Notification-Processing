using NotificationProcessing.Api.Domain;

namespace NotificationProcessing.Api.Contracts;

public sealed record NotificationResponse(
    Guid Id,
    string CustomerId,
    NotificationType Type,
    string Recipient,
    string? Subject,
    string Message,
    NotificationPriority Priority,
    DateTimeOffset RequestedDeliveryTime,
    DateTimeOffset CreatedAt,
    NotificationStatus Status,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SentAt,
    string? LastError)
{
    public static NotificationResponse From(Notification n) => new(
        n.Id, n.CustomerId, n.Type, n.Recipient, n.Subject, n.Message, n.Priority, n.RequestedDeliveryTime,
        n.CreatedAt, n.Status, n.AttemptCount, n.LastAttemptAt, n.SentAt, n.LastError);
}
