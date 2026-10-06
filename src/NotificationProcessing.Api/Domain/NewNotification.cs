namespace NotificationProcessing.Api.Domain;

/// <summary>
/// A notification request that has passed validation, with every field parsed into its domain type.
/// </summary>
/// <param name="RequestedDeliveryTime">
/// Null when the caller did not specify one, meaning "send as soon as possible". Kept nullable (rather than
/// defaulted to "now" during validation) so that retries of the same request produce the same fingerprint.
/// </param>
public sealed record NewNotification(
    string CustomerId,
    NotificationType Type,
    string Recipient,
    string? Subject,
    string Message,
    NotificationPriority Priority,
    DateTimeOffset? RequestedDeliveryTime);
