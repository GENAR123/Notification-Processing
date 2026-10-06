using NotificationProcessing.Api.Contracts;
using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Services;
using NotificationProcessing.Api.Validation;

namespace NotificationProcessing.Tests;

public class CreateNotificationRequestValidatorTests
{
    private static readonly DateTimeOffset Now = TestContext.Start;
    private readonly CreateNotificationRequestValidator _validator =
        new(new FakeTimeProvider(Now), Microsoft.Extensions.Options.Options.Create(new NotificationOptions()));

    private static CreateNotificationRequest Valid() => new(
        "CUST-101", "Email", "customer@example.com", "Order Confirmed", "Your order has been confirmed.", "Normal", Now.AddHours(1));

    [Fact]
    public void Valid_request_is_parsed_into_domain_types()
    {
        var result = _validator.Validate(Valid() with { Type = "email", Priority = "HIGH", Recipient = "  customer@example.com " });

        Assert.True(result.IsValid);
        Assert.Equal(NotificationType.Email, result.Value!.Type);
        Assert.Equal(NotificationPriority.High, result.Value.Priority);
        Assert.Equal("customer@example.com", result.Value.Recipient);
    }

    [Fact]
    public void All_errors_are_reported_together()
    {
        var result = _validator.Validate(new CreateNotificationRequest(null, null, null, null, null, null, null));

        Assert.False(result.IsValid);
        Assert.Equivalent(new[] { "customerId", "type", "recipient", "message" }, result.Errors.Keys.ToArray(), strict: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_required_fields_are_rejected(string blank)
    {
        var result = _validator.Validate(Valid() with { CustomerId = blank, Message = blank, Recipient = blank });

        Assert.Contains("customerId", result.Errors.Keys);
        Assert.Contains("message", result.Errors.Keys);
        Assert.Contains("recipient", result.Errors.Keys);
    }

    [Theory]
    [InlineData("plainaddress")]
    [InlineData("missing-at.example.com")]
    [InlineData("@example.com")]
    [InlineData("user@")]
    [InlineData("user@localhost")]
    [InlineData("user@example.")]
    [InlineData("user name@example.com")]
    [InlineData("Customer <customer@example.com>")]
    [InlineData("a@b@example.com")]
    public void Invalid_email_addresses_are_rejected(string recipient)
    {
        var result = _validator.Validate(Valid() with { Recipient = recipient });

        Assert.False(result.IsValid);
        Assert.Contains("recipient", result.Errors.Keys);
    }

    [Theory]
    [InlineData("customer@example.com")]
    [InlineData("first.last+tag@sub.example.co.uk")]
    public void Valid_email_addresses_are_accepted(string recipient) =>
        Assert.True(_validator.Validate(Valid() with { Recipient = recipient }).IsValid);

    [Theory]
    [InlineData("Urgent")]
    [InlineData("")]
    [InlineData("2")] // Enum.TryParse would accept numeric strings; we must not.
    public void Unknown_priority_is_rejected(string priority)
    {
        var result = _validator.Validate(Valid() with { Priority = priority });

        Assert.Contains("priority", result.Errors.Keys);
    }

    [Fact]
    public void Missing_priority_defaults_to_normal()
    {
        var result = _validator.Validate(Valid() with { Priority = null });

        Assert.Equal(NotificationPriority.Normal, result.Value!.Priority);
    }

    [Theory]
    [InlineData("Sms")]
    [InlineData("0")]
    public void Unsupported_type_is_rejected(string type) =>
        Assert.Contains("type", _validator.Validate(Valid() with { Type = type }).Errors.Keys);

    [Fact]
    public void Delivery_time_exactly_now_is_accepted() =>
        Assert.True(_validator.Validate(Valid() with { RequestedDeliveryTime = Now }).IsValid);

    [Fact]
    public void Delivery_time_slightly_in_the_past_within_clock_skew_tolerance_is_accepted() =>
        Assert.True(_validator.Validate(Valid() with { RequestedDeliveryTime = Now.AddSeconds(-5) }).IsValid);

    [Fact]
    public void Delivery_time_in_the_past_beyond_tolerance_is_rejected()
    {
        var result = _validator.Validate(Valid() with { RequestedDeliveryTime = Now.AddMinutes(-5) });

        Assert.Contains("requestedDeliveryTime", result.Errors.Keys);
    }

    [Fact]
    public void Delivery_time_is_compared_as_an_instant_regardless_of_offset()
    {
        // 13:30 at +02:00 is 11:30 UTC, i.e. 30 minutes before "now" (12:00 UTC).
        var pastInUtc = new DateTimeOffset(2026, 10, 6, 13, 30, 0, TimeSpan.FromHours(2));

        Assert.Contains("requestedDeliveryTime", _validator.Validate(Valid() with { RequestedDeliveryTime = pastInUtc }).Errors.Keys);
    }

    [Fact]
    public void Missing_delivery_time_is_accepted_and_left_unset() =>
        Assert.Null(_validator.Validate(Valid() with { RequestedDeliveryTime = null }).Value!.RequestedDeliveryTime);

    [Fact]
    public void Oversized_message_is_rejected() =>
        Assert.Contains("message", _validator.Validate(Valid() with
        {
            Message = new string('x', CreateNotificationRequestValidator.MaxMessageLength + 1)
        }).Errors.Keys);
}
