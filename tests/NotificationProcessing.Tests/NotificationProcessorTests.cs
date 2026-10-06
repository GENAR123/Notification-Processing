using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Providers;

namespace NotificationProcessing.Tests;

public class NotificationProcessorTests
{
    private readonly TestContext _ctx = new();

    [Fact]
    public async Task High_priority_is_sent_before_normal_and_low_regardless_of_arrival_order()
    {
        var low = await _ctx.CreateAsync(TestContext.NewEmail("low", NotificationPriority.Low));
        var normal = await _ctx.CreateAsync(TestContext.NewEmail("normal", NotificationPriority.Normal));
        var high = await _ctx.CreateAsync(TestContext.NewEmail("high", NotificationPriority.High));

        await _ctx.Processor.ProcessPendingAsync();

        Assert.Equal([high.Id, normal.Id, low.Id], _ctx.Provider.Attempts.Select(n => n.Id));
    }

    [Fact]
    public async Task Same_priority_is_sent_in_arrival_order_even_with_identical_timestamps()
    {
        // Clock is frozen, so CreatedAt is equal for all; ordering must still be deterministic FIFO.
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add((await _ctx.CreateAsync(TestContext.NewEmail($"message {i}"))).Id);

        await _ctx.Processor.ProcessPendingAsync();

        Assert.Equal(ids, _ctx.Provider.Attempts.Select(n => n.Id));
    }

    [Fact]
    public async Task Same_priority_with_earlier_delivery_time_is_sent_first()
    {
        var later = await _ctx.CreateAsync(TestContext.NewEmail("later", deliverAt: TestContext.Start.AddMinutes(10)));
        var earlier = await _ctx.CreateAsync(TestContext.NewEmail("earlier", deliverAt: TestContext.Start.AddMinutes(5)));
        _ctx.Time.Advance(TimeSpan.FromMinutes(15));

        await _ctx.Processor.ProcessPendingAsync();

        Assert.Equal([earlier.Id, later.Id], _ctx.Provider.Attempts.Select(n => n.Id));
    }

    [Fact]
    public async Task Scheduled_notifications_are_not_sent_before_their_delivery_time()
    {
        var scheduled = await _ctx.CreateAsync(TestContext.NewEmail(deliverAt: TestContext.Start.AddHours(1)));

        var early = await _ctx.Processor.ProcessPendingAsync();
        Assert.Equal(0, early.Attempted);
        Assert.Equal(NotificationStatus.Pending, (await _ctx.GetAsync(scheduled.Id)).Status);

        _ctx.Time.Advance(TimeSpan.FromHours(1)); // exactly due
        var onTime = await _ctx.Processor.ProcessPendingAsync();
        Assert.Equal(1, onTime.Sent);
    }

    [Fact]
    public async Task Successful_send_marks_notification_sent_and_it_is_never_sent_again()
    {
        var notification = await _ctx.CreateAsync(TestContext.NewEmail());

        await _ctx.Processor.ProcessPendingAsync();
        await _ctx.Processor.ProcessPendingAsync();

        var stored = await _ctx.GetAsync(notification.Id);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(TestContext.Start, stored.SentAt);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Single(_ctx.Provider.Attempts);
    }

    [Fact]
    public async Task Provider_exception_does_not_stop_the_batch_and_failed_notification_is_retried_later()
    {
        var failing = await _ctx.CreateAsync(TestContext.NewEmail("fails", NotificationPriority.High));
        var ok = await _ctx.CreateAsync(TestContext.NewEmail("works"));
        _ctx.Provider.Behavior = n => n.Id == failing.Id
            ? throw new HttpRequestException("SMTP timeout")
            : NotificationProviderResult.Success();

        var summary = await _ctx.Processor.ProcessPendingAsync();

        Assert.Equal(new(Attempted: 2, Sent: 1, Retrying: 1, Failed: 0), summary);
        var afterFailure = await _ctx.GetAsync(failing.Id);
        Assert.Equal(NotificationStatus.Pending, afterFailure.Status);
        Assert.Equal(1, afterFailure.AttemptCount);
        Assert.Equal("SMTP timeout", afterFailure.LastError);
        Assert.Equal(NotificationStatus.Sent, (await _ctx.GetAsync(ok.Id)).Status);

        // Provider recovers: next run delivers it.
        _ctx.Provider.Behavior = _ => NotificationProviderResult.Success();
        await _ctx.Processor.ProcessPendingAsync();
        var recovered = await _ctx.GetAsync(failing.Id);
        Assert.Equal(NotificationStatus.Sent, recovered.Status);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public async Task Notification_is_marked_failed_after_max_attempts_and_not_retried_further()
    {
        var notification = await _ctx.CreateAsync(TestContext.NewEmail());
        _ctx.Provider.Behavior = _ => NotificationProviderResult.TransientFailure("Service unavailable");

        for (var run = 0; run < _ctx.Options.MaxSendAttempts + 2; run++)
            await _ctx.Processor.ProcessPendingAsync();

        var stored = await _ctx.GetAsync(notification.Id);
        Assert.Equal(NotificationStatus.Failed, stored.Status);
        Assert.Equal(_ctx.Options.MaxSendAttempts, stored.AttemptCount);
        Assert.Equal(_ctx.Options.MaxSendAttempts, _ctx.Provider.Attempts.Count);
    }

    [Fact]
    public async Task Permanent_provider_failure_is_marked_failed_without_retrying()
    {
        var notification = await _ctx.CreateAsync(TestContext.NewEmail());
        _ctx.Provider.Behavior = _ => NotificationProviderResult.PermanentFailure("Mailbox does not exist");

        await _ctx.Processor.ProcessPendingAsync();
        await _ctx.Processor.ProcessPendingAsync();

        var stored = await _ctx.GetAsync(notification.Id);
        Assert.Equal(NotificationStatus.Failed, stored.Status);
        Assert.Equal("Mailbox does not exist", stored.LastError);
        Assert.Single(_ctx.Provider.Attempts);
    }

    [Fact]
    public async Task Concurrent_processing_runs_never_send_the_same_notification_twice()
    {
        for (var i = 0; i < 20; i++)
            await _ctx.CreateAsync(TestContext.NewEmail($"message {i}"));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => _ctx.Processor.ProcessPendingAsync())));

        var attempts = _ctx.Provider.Attempts;
        Assert.Equal(20, attempts.Count);
        Assert.Equal(20, attempts.Select(n => n.Id).Distinct().Count());
    }

    [Fact]
    public async Task Batch_size_limits_how_many_are_processed_per_run_highest_priority_first()
    {
        _ctx.Options.ProcessingBatchSize = 2;
        await _ctx.CreateAsync(TestContext.NewEmail("low", NotificationPriority.Low));
        var high1 = await _ctx.CreateAsync(TestContext.NewEmail("high 1", NotificationPriority.High));
        var high2 = await _ctx.CreateAsync(TestContext.NewEmail("high 2", NotificationPriority.High));

        var summary = await _ctx.Processor.ProcessPendingAsync();

        Assert.Equal(2, summary.Sent);
        Assert.Equal([high1.Id, high2.Id], _ctx.Provider.Attempts.Select(n => n.Id));
    }
}
