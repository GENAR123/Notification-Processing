namespace NotificationProcessing.Api.Domain;

public enum NotificationType
{
    Email
}

// Numeric values are significant: higher value = processed first.
public enum NotificationPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public enum NotificationStatus
{
    Pending,
    Processing,
    Sent,
    Failed
}
