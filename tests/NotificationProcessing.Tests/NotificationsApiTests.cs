using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationProcessing.Api.Contracts;
using NotificationProcessing.Api.Domain;
using NotificationProcessing.Api.Providers;
using NotificationProcessing.Api.Services;

namespace NotificationProcessing.Tests;

/// <summary>
/// End-to-end through the HTTP pipeline: routing, model binding, status codes and response shapes.
/// Business rules are covered in depth by the unit tests; these check the wiring and the API contract.
/// </summary>
public sealed class NotificationsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly FakeTimeProvider _time = new(TestContext.Start);
    private readonly FakeNotificationProvider _provider = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public NotificationsApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(_time);
                services.RemoveAll<INotificationProvider>();
                services.AddSingleton<INotificationProvider>(_provider);
            }));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object ValidRequest(string message = "Your order has been confirmed.", string customerId = "CUST-101") => new
    {
        customerId,
        type = "Email",
        recipient = "customer@example.com",
        subject = "Order Confirmed",
        message,
        priority = "Normal",
        requestedDeliveryTime = TestContext.Start.AddMinutes(30)
    };

    [Fact]
    public async Task Post_valid_request_returns_201_with_location_of_pending_notification()
    {
        var response = await _client.PostAsJsonAsync("/api/notifications", ValidRequest());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<NotificationResponse>(Json);
        Assert.Equal(NotificationStatus.Pending, created!.Status);
        Assert.Equal(TestContext.Start, created.CreatedAt);

        var fetched = await _client.GetFromJsonAsync<NotificationResponse>(response.Headers.Location, Json);
        Assert.Equal(created.Id, fetched!.Id);
    }

    [Fact]
    public async Task Post_invalid_request_returns_400_with_errors_per_field()
    {
        var response = await _client.PostAsJsonAsync("/api/notifications", new
        {
            customerId = "",
            type = "Email",
            recipient = "not-an-email",
            message = "Hi",
            priority = "Urgent",
            requestedDeliveryTime = TestContext.Start.AddDays(-1)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Json);
        Assert.Equivalent(new[] { "customerId", "recipient", "priority", "requestedDeliveryTime" }, problem!.Errors.Keys.ToArray(), strict: true);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "customerId": "CUST-101", "requestedDeliveryTime": "not-a-date" }""")]
    [InlineData("")]
    public async Task Post_malformed_body_returns_400(string body)
    {
        var response = await _client.PostAsync("/api/notifications", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_duplicate_request_returns_200_with_original_notification()
    {
        var first = await _client.PostAsJsonAsync("/api/notifications", ValidRequest());
        var second = await _client.PostAsJsonAsync("/api/notifications", ValidRequest());

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        var original = await first.Content.ReadFromJsonAsync<NotificationResponse>(Json);
        var replayed = await second.Content.ReadFromJsonAsync<NotificationResponse>(Json);
        Assert.Equal(original!.Id, replayed!.Id);
    }

    [Fact]
    public async Task Get_unknown_id_returns_404_problem_details()
    {
        var response = await _client.GetAsync($"/api/notifications/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_with_non_guid_id_returns_404()
    {
        var response = await _client.GetAsync("/api/notifications/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_by_customer_returns_only_that_customers_notifications_newest_first()
    {
        await _client.PostAsJsonAsync("/api/notifications", ValidRequest("first"));
        _time.Advance(TimeSpan.FromSeconds(1));
        await _client.PostAsJsonAsync("/api/notifications", ValidRequest("second"));
        await _client.PostAsJsonAsync("/api/notifications", ValidRequest("other customer", customerId: "CUST-999"));

        var list = await _client.GetFromJsonAsync<List<NotificationResponse>>("/api/customers/CUST-101/notifications", Json);

        Assert.Equal(["second", "first"], list!.Select(n => n.Message));
    }

    [Fact]
    public async Task Get_by_unknown_customer_returns_empty_list()
    {
        var list = await _client.GetFromJsonAsync<List<NotificationResponse>>("/api/customers/NOBODY/notifications", Json);

        Assert.Empty(list!);
    }

    [Fact]
    public async Task Process_sends_due_notifications_and_reports_summary()
    {
        var created = await (await _client.PostAsJsonAsync("/api/notifications", ValidRequest()))
            .Content.ReadFromJsonAsync<NotificationResponse>(Json);
        _time.Advance(TimeSpan.FromMinutes(30));

        var response = await _client.PostAsync("/api/notifications/process", null);

        Assert.Equal(new ProcessingSummary(1, 1, 0, 0), await response.Content.ReadFromJsonAsync<ProcessingSummary>(Json));
        var fetched = await _client.GetFromJsonAsync<NotificationResponse>($"/api/notifications/{created!.Id}", Json);
        Assert.Equal(NotificationStatus.Sent, fetched!.Status);
    }

    [Fact]
    public async Task Process_survives_provider_failure_and_reports_it()
    {
        await _client.PostAsJsonAsync("/api/notifications", ValidRequest());
        _time.Advance(TimeSpan.FromMinutes(30));
        _provider.Behavior = _ => throw new InvalidOperationException("Provider exploded");

        var response = await _client.PostAsync("/api/notifications/process", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ProcessingSummary(1, 0, 1, 0), await response.Content.ReadFromJsonAsync<ProcessingSummary>(Json));
    }
}
