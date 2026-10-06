namespace NotificationProcessing.Api.Contracts;

/// <summary>
/// Incoming request body. Fields are deliberately loosely typed (nullable strings rather than enums) so the
/// validator can report every problem in a single response with a clear message, instead of the request
/// failing on the first JSON binding error.
/// </summary>
public sealed record CreateNotificationRequest(
    string? CustomerId,
    string? Type,
    string? Recipient,
    string? Subject,
    string? Message,
    string? Priority,
    DateTimeOffset? RequestedDeliveryTime);
