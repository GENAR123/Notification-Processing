using NotificationProcessing.Api.Domain;

namespace NotificationProcessing.Tests;

public class DuplicateDetectionTests
{
    private readonly TestContext _ctx = new();

    [Fact]
    public async Task Identical_request_within_window_returns_the_original_notification()
    {
        var first = await _ctx.Service.CreateAsync(TestContext.NewEmail(), default);
        _ctx.Time.Advance(TimeSpan.FromMinutes(5));
        var second = await _ctx.Service.CreateAsync(TestContext.NewEmail(), default);

        Assert.False(first.IsDuplicate);
        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Notification.Id, second.Notification.Id);
        Assert.Single(await _ctx.Repository.GetByCustomerAsync("CUST-101", default));
    }

    [Fact]
    public async Task Duplicate_of_an_already_sent_notification_is_not_sent_again()
    {
        await _ctx.CreateAsync(TestContext.NewEmail());
        await _ctx.Processor.ProcessPendingAsync();

        var retry = await _ctx.Service.CreateAsync(TestContext.NewEmail(), default);
        await _ctx.Processor.ProcessPendingAsync();

        Assert.True(retry.IsDuplicate);
        Assert.Equal(NotificationStatus.Sent, retry.Notification.Status);
        Assert.Single(_ctx.Provider.Attempts);
    }

    [Fact]
    public async Task Identical_request_after_window_is_treated_as_new()
    {
        var first = await _ctx.CreateAsync(TestContext.NewEmail());
        _ctx.Time.Advance(_ctx.Options.DuplicateWindow + TimeSpan.FromSeconds(1));
        var second = await _ctx.Service.CreateAsync(TestContext.NewEmail(), default);

        Assert.False(second.IsDuplicate);
        Assert.NotEqual(first.Id, second.Notification.Id);
    }

    [Fact]
    public async Task Requests_differing_in_any_content_field_are_not_duplicates()
    {
        var baseline = await _ctx.CreateAsync(TestContext.NewEmail());

        var variants = new[]
        {
            TestContext.NewEmail(message: "Different message"),
            TestContext.NewEmail(priority: NotificationPriority.High),
            TestContext.NewEmail(customerId: "CUST-202"),
            TestContext.NewEmail(recipient: "other@example.com"),
            TestContext.NewEmail(deliverAt: TestContext.Start.AddHours(1)),
        };

        foreach (var variant in variants)
        {
            var result = await _ctx.Service.CreateAsync(variant, default);
            Assert.False(result.IsDuplicate);
            Assert.NotEqual(baseline.Id, result.Notification.Id);
        }
    }

    [Fact]
    public async Task Recipient_case_does_not_defeat_duplicate_detection()
    {
        var first = await _ctx.CreateAsync(TestContext.NewEmail(recipient: "Customer@Example.com"));
        var second = await _ctx.Service.CreateAsync(TestContext.NewEmail(recipient: "customer@example.com"), default);

        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Id, second.Notification.Id);
    }

    [Fact]
    public async Task Retries_without_a_delivery_time_are_duplicates_even_though_time_has_passed()
    {
        // Omitted delivery time defaults to "now" at storage, but the fingerprint must use the request as sent.
        var first = await _ctx.CreateAsync(TestContext.NewEmail(deliverAt: null));
        _ctx.Time.Advance(TimeSpan.FromSeconds(30));
        var second = await _ctx.Service.CreateAsync(TestContext.NewEmail(deliverAt: null), default);

        Assert.True(second.IsDuplicate);
        Assert.Equal(first.Id, second.Notification.Id);
    }

    [Fact]
    public async Task Concurrent_identical_requests_store_exactly_one_notification()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => _ctx.Service.CreateAsync(TestContext.NewEmail(), default))));

        Assert.Single(results, r => !r.IsDuplicate);
        Assert.Single(results.Select(r => r.Notification.Id).Distinct());
    }
}
