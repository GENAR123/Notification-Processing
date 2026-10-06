using System.Net.Mail;

namespace NotificationProcessing.Api.Validation;

public static class EmailAddressValidator
{
    private const int MaxLength = 254; // RFC 5321 path limit

    /// <summary>
    /// Pragmatic check: a single bare address (no display name) with a dotted domain. This rejects typos and
    /// garbage; only actually delivering mail proves an address exists.
    /// </summary>
    public static bool IsValid(string value)
    {
        if (value.Length > MaxLength || value.Any(char.IsWhiteSpace))
            return false;

        // MailAddress also accepts forms like "Name <a@b.com>"; requiring Address == input rejects those.
        if (!MailAddress.TryCreate(value, out var address) || address.Address != value)
            return false;

        var domain = address.Host;
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
