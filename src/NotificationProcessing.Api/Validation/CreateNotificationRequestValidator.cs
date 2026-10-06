using Microsoft.Extensions.Options;
using NotificationProcessing.Api.Contracts;
using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Services;

namespace NotificationProcessing.Api.Validation;

public sealed record RequestValidationResult(NewNotification? Value, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Value is not null;
}

/// <summary>
/// Validates an incoming request and, if valid, parses it into a <see cref="NewNotification"/>.
/// All errors are collected so the caller can fix everything in one round trip.
/// </summary>
public sealed class CreateNotificationRequestValidator(TimeProvider timeProvider, IOptions<NotificationOptions> options)
{
    public const int MaxCustomerIdLength = 100;
    public const int MaxSubjectLength = 200;
    public const int MaxMessageLength = 10_000;

    public RequestValidationResult Validate(CreateNotificationRequest request)
    {
        var errors = new Dictionary<string, List<string>>();
        void AddError(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
                errors[field] = list = [];
            list.Add(message);
        }

        var customerId = request.CustomerId?.Trim();
        if (string.IsNullOrEmpty(customerId))
            AddError("customerId", "Customer ID is required.");
        else if (customerId.Length > MaxCustomerIdLength)
            AddError("customerId", $"Customer ID must be at most {MaxCustomerIdLength} characters.");

        NotificationType? type = null;
        if (string.IsNullOrWhiteSpace(request.Type))
            AddError("type", "Notification type is required.");
        else if (TryParseName<NotificationType>(request.Type, out var parsedType))
            type = parsedType;
        else
            AddError("type", $"Notification type '{request.Type}' is not supported. Supported types: {NamesOf<NotificationType>()}.");

        var recipient = request.Recipient?.Trim();
        if (string.IsNullOrEmpty(recipient))
            AddError("recipient", "Recipient is required.");
        else if (type == NotificationType.Email && !EmailAddressValidator.IsValid(recipient))
            AddError("recipient", "Recipient must be a valid email address.");

        var subject = string.IsNullOrWhiteSpace(request.Subject) ? null : request.Subject.Trim();
        if (subject?.Length > MaxSubjectLength)
            AddError("subject", $"Subject must be at most {MaxSubjectLength} characters.");

        if (string.IsNullOrWhiteSpace(request.Message))
            AddError("message", "Message is required.");
        else if (request.Message.Length > MaxMessageLength)
            AddError("message", $"Message must be at most {MaxMessageLength} characters.");

        // Priority is optional (defaults to Normal), but if supplied it must be one of the known values.
        var priority = NotificationPriority.Normal;
        if (request.Priority is not null)
        {
            if (TryParseName<NotificationPriority>(request.Priority, out var parsedPriority))
                priority = parsedPriority;
            else
                AddError("priority", $"Priority must be one of: {NamesOf<NotificationPriority>()}.");
        }

        // A time equal to "now" (or slightly before, within tolerance) is valid and means "send immediately".
        var earliestAllowed = timeProvider.GetUtcNow() - options.Value.DeliveryTimeTolerance;
        if (request.RequestedDeliveryTime < earliestAllowed)
            AddError("requestedDeliveryTime", "Requested delivery time cannot be in the past.");

        if (errors.Count > 0)
            return new RequestValidationResult(null, errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));

        return new RequestValidationResult(
            new NewNotification(customerId!, type!.Value, recipient!, subject, request.Message!, priority,
                request.RequestedDeliveryTime?.ToUniversalTime()),
            new Dictionary<string, string[]>());
    }

    // Enum.TryParse would also accept numeric strings like "2" or "7"; only accept the declared names.
    private static bool TryParseName<TEnum>(string value, out TEnum result) where TEnum : struct, Enum
    {
        var name = Enum.GetNames<TEnum>().FirstOrDefault(n => n.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        result = name is null ? default : Enum.Parse<TEnum>(name);
        return name is not null;
    }

    private static string NamesOf<TEnum>() where TEnum : struct, Enum => string.Join(", ", Enum.GetNames<TEnum>());
}
