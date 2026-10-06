using System.Text.Json.Serialization;
using NotificationProcessing.Api.Persistence;
using NotificationProcessing.Api.Providers;
using NotificationProcessing.Api.Services;
using NotificationProcessing.Api.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.Configure<SimulatedEmailProviderOptions>(builder.Configuration.GetSection(SimulatedEmailProviderOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<INotificationRepository, InMemoryNotificationRepository>();
builder.Services.AddSingleton<INotificationProvider, SimulatedEmailProvider>();
builder.Services.AddSingleton<CreateNotificationRequestValidator>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<NotificationProcessor>();

var app = builder.Build();

// Unhandled exceptions become an RFC 7807 500 response instead of leaking details; bare status codes get a ProblemDetails body.
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapControllers();

app.Run();

public partial class Program; // exposed for WebApplicationFactory in integration tests
