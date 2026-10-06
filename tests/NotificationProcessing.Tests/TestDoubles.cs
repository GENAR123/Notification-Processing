using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Persistence;
using NotificationProcessing.Api.Providers;
using NotificationProcessing.Api.Services;

namespace NotificationProcessing.Tests;

public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records every send attempt; behaviour per notification can be overridden (including throwing).</summary>
public sealed class FakeNotificationProvider : INotificationProvider
{
    private readonly List<Notification> _attempts = [];

    public NotificationType Type => NotificationType.Email;
    public Func<Notification, NotificationProviderResult> Behavior { get; set; } = _ => NotificationProviderResult.Success();

    public IReadOnlyList<Notification> Attempts { get { lock (_attempts) return _attempts.ToList(); } }

    public Task<NotificationProviderResult> SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        lock (_attempts) _attempts.Add(notification);
        return Task.FromResult(Behavior(notification));
    }
}

/// <summary>Wires the real service, processor and in-memory repository around fake time and a fake provider.</summary>
public sealed class TestContext
{
    public static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(Start);
    public FakeNotificationProvider Provider { get; } = new();
    public InMemoryNotificationRepository Repository { get; } = new();
    public NotificationOptions Options { get; } = new();
    public NotificationService Service { get; }
    public NotificationProcessor Processor { get; }

    public TestContext()
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        Service = new NotificationService(Repository, Time, options, NullLogger<NotificationService>.Instance);
        Processor = new NotificationProcessor(Repository, [Provider], Time, options, NullLogger<NotificationProcessor>.Instance);
    }

    public static NewNotification NewEmail(
        string message = "Your order has been confirmed.",
        NotificationPriority priority = NotificationPriority.Normal,
        DateTimeOffset? deliverAt = null,
        string customerId = "CUST-101",
        string recipient = "customer@example.com") =>
        new(customerId, NotificationType.Email, recipient, "Order Confirmed", message, priority, deliverAt);

    public async Task<Notification> CreateAsync(NewNotification request) =>
        (await Service.CreateAsync(request, CancellationToken.None)).Notification;

    public async Task<Notification> GetAsync(Guid id) =>
        (await Repository.GetByIdAsync(id, CancellationToken.None))!;
}
