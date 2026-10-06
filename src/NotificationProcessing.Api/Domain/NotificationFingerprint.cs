using System.Security.Cryptography;
using System.Text;

namespace NotificationProcessing.Api.Domain;

/// <summary>
/// Computes a stable hash of a notification request's content. Two requests with the same fingerprint are
/// considered "the exact same request" for duplicate detection.
/// </summary>
public static class NotificationFingerprint
{
    private const char Separator = '\u001F'; // ASCII unit separator: cannot be confused with field content boundaries

    public static string Compute(NewNotification notification)
    {
        var canonical = string.Join(Separator,
            notification.CustomerId,
            notification.Type,
            // Email addresses are matched case-insensitively in practice, so "A@x.com" and "a@x.com" are the same recipient.
            notification.Recipient.ToLowerInvariant(),
            notification.Subject ?? string.Empty,
            notification.Message,
            notification.Priority,
            notification.RequestedDeliveryTime?.UtcDateTime.ToString("O") ?? "asap");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
