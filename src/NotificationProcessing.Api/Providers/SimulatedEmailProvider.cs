using Microsoft.Extensions.Options;
using NotificationProcessing.Api.Domain;

namespace NotificationProcessing.Api.Providers;

public sealed class SimulatedEmailProviderOptions
{
    public const string SectionName = "SimulatedEmailProvider";

    /// <summary>Probability (0..1) that a send throws, to exercise failure handling.</summary>
    public double FailureRate { get; set; }
}

/// <summary>Stands in for a real email gateway: logs instead of sending, and can be configured to fail randomly.</summary>
public sealed class SimulatedEmailProvider(
    IOptions<SimulatedEmailProviderOptions> options,
    ILogger<SimulatedEmailProvider> logger) : INotificationProvider
{
    public NotificationType Type => NotificationType.Email;

    public async Task<NotificationProviderResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken); // simulated network latency

        if (Random.Shared.NextDouble() < options.Value.FailureRate)
            throw new HttpRequestException("Simulated email provider outage.");

        logger.LogInformation(
            "Simulated email sent. NotificationId={NotificationId} CustomerId={CustomerId} Subject={Subject}",
            notification.Id, notification.CustomerId, notification.Subject);

        return NotificationProviderResult.Success();
    }
}
