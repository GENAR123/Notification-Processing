using NotificationProcessing.Api.Domain;

namespace NotificationProcessing.Api.Providers;

/// <summary>
/// An external delivery channel (email gateway, SMS gateway, ...). One implementation per notification type.
/// Implementations may either return a failure result or throw; the processor handles both.
/// </summary>
public interface INotificationProvider
{
    NotificationType Type { get; }

    Task<NotificationProviderResult> SendAsync(Notification notification, CancellationToken cancellationToken);
}

/// <param name="IsPermanent">
/// True when retrying cannot help (e.g. the provider rejected the recipient), so the notification should be
/// marked Failed immediately instead of being retried.
/// </param>
public sealed record NotificationProviderResult(bool Succeeded, string? Error = null, bool IsPermanent = false)
{
    public static NotificationProviderResult Success() => new(true);
    public static NotificationProviderResult TransientFailure(string error) => new(false, error);
    public static NotificationProviderResult PermanentFailure(string error) => new(false, error, IsPermanent: true);
}
