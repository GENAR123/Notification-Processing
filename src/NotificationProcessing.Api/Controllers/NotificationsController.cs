using Microsoft.AspNetCore.Mvc;
using NotificationProcessing.Api.Contracts;
using NotificationProcessing.Api.Services;
using NotificationProcessing.Api.Validation;

namespace NotificationProcessing.Api.Controllers;

/// <summary>HTTP concerns only: map requests to service calls and results to status codes.</summary>
[ApiController]
[Route("api")]
public sealed class NotificationsController(
    CreateNotificationRequestValidator validator,
    NotificationService notificationService,
    NotificationProcessor processor) : ControllerBase
{
    /// <summary>
    /// Accepts a notification. Returns 201 for a new notification, or 200 with the original notification
    /// (and an Idempotent-Replayed header) when the identical request was already accepted recently.
    /// </summary>
    [HttpPost("notifications")]
    [ProducesResponseType<NotificationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<NotificationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NotificationResponse>> Create(CreateNotificationRequest request, CancellationToken cancellationToken)
    {
        var validation = validator.Validate(request);
        if (!validation.IsValid)
        {
            foreach (var (field, messages) in validation.Errors)
                foreach (var message in messages)
                    ModelState.AddModelError(field, message);
            return ValidationProblem(ModelState);
        }

        var result = await notificationService.CreateAsync(validation.Value!, cancellationToken);
        var response = NotificationResponse.From(result.Notification);

        if (result.IsDuplicate)
        {
            Response.Headers["Idempotent-Replayed"] = "true";
            return Ok(response);
        }

        return CreatedAtAction(nameof(GetById), new { id = response.Id }, response);
    }

    [HttpGet("notifications/{id:guid}")]
    [ProducesResponseType<NotificationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NotificationResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var notification = await notificationService.GetByIdAsync(id, cancellationToken);
        return notification is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Notification not found.", detail: $"No notification exists with id '{id}'.")
            : Ok(NotificationResponse.From(notification));
    }

    /// <summary>Returns the customer's notifications, newest first. An unknown customer yields an empty list.</summary>
    [HttpGet("customers/{customerId}/notifications")]
    [ProducesResponseType<IReadOnlyList<NotificationResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<NotificationResponse>>> GetByCustomer(string customerId, CancellationToken cancellationToken)
    {
        var notifications = await notificationService.GetByCustomerAsync(customerId, cancellationToken);
        return Ok(notifications.Select(NotificationResponse.From).ToList());
    }

    /// <summary>Processes one batch of due Pending notifications and reports the outcome.</summary>
    [HttpPost("notifications/process")]
    [ProducesResponseType<ProcessingSummary>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ProcessingSummary>> Process(CancellationToken cancellationToken) =>
        Ok(await processor.ProcessPendingAsync(cancellationToken));
}
