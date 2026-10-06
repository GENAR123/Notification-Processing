# Notification Processing API

A small ASP.NET Core (.NET 10) Web API that validates, stores and processes customer notifications. Only email is supported for now.

## 1. How to run

You need the .NET 10 SDK.

```bash
dotnet run --project src/NotificationProcessing.Api --launch-profile http   # http://localhost:5264
dotnet test                                                                 # 56 tests
```

[`NotificationProcessing.Api.http`](src/NotificationProcessing.Api/NotificationProcessing.Api.http) has sample requests you can run from VS Code or Rider.

| Method | Route | Purpose | Responses |
|---|---|---|---|
| `POST` | `/api/notifications` | Create a notification | `201` created · `200` duplicate (with `Idempotent-Replayed: true`) · `400` validation errors |
| `GET` | `/api/notifications/{id}` | Get one notification | `200` · `404` |
| `GET` | `/api/customers/{customerId}/notifications` | Get a customer's notifications, newest first | `200` (an empty list for an unknown customer) |
| `POST` | `/api/notifications/process` | Process one batch of due pending notifications | `200` with a summary `{attempted, sent, retrying, failed}` |

You can change the settings in `appsettings.json`: `Notifications:DuplicateWindow`, `DeliveryTimeTolerance`, `MaxSendAttempts` and `ProcessingBatchSize`. To see failure handling, set `SimulatedEmailProvider:FailureRate` to a value from 0 to 1. For example, `0.3` makes 30% of sends throw an exception.

## 2. Design

```
Controllers/   HTTP only: map requests to service calls, and results to status codes
Contracts/     Request and response DTOs (the API's public shape)
Validation/    Request validation. It turns a raw request into a typed NewNotification
Domain/        Notification entity, enums, status transitions, fingerprint for duplicates
Services/      NotificationService (create/query), NotificationProcessor (send), options
Persistence/   INotificationRepository + thread-safe in-memory implementation
Providers/     INotificationProvider (external channel) + SimulatedEmailProvider
```

**Request flow.** The controller passes the request to `CreateNotificationRequestValidator`. If the request is invalid, the controller returns a `ValidationProblemDetails` that lists every error, keyed by field. If it is valid, the validator returns a typed `NewNotification`, so nothing later has to parse strings again. `NotificationService` then stores the notification as `Pending`, or returns the existing one if the request is a duplicate.

**Processing.** `NotificationProcessor` asks the repository for due `Pending` notifications in this order: priority (High → Normal → Low), then requested delivery time, then arrival order. It claims each one by moving it from `Pending` to `Processing` in a single atomic step. It then calls the provider for that notification's type and records the result. Notifications are processed one at a time, which keeps the priority order strict.

**Abstractions.** I added only two interfaces. Each one has a real reason to be swapped:
- `INotificationRepository`: the in-memory store would become a database. The interface makes the operations that must be atomic explicit: "add unless duplicate" and "claim".
- `INotificationProvider`: the external gateway. It is keyed by `NotificationType`, so adding SMS means adding one class.

`TimeProvider` is injected so tests can control the clock. The validator and services are concrete classes because they have only one implementation.

**Status lifecycle**
```
Pending ──claim──▶ Processing ──success──▶ Sent
   ▲                   │
   └── transient failure, attempts < MaxSendAttempts
                       │
                       └── permanent failure, or attempts exhausted ──▶ Failed
```

**Duplicate requests.** Each request gets a fingerprint. This is a SHA-256 hash of its content: customer, type, recipient (lower-cased), subject, message, priority and requested delivery time. If an identical request arrives within `DuplicateWindow` (10 minutes by default), the API stores nothing new. It returns the original notification with `200 OK` and the header `Idempotent-Replayed: true`. A caller retrying after a timeout therefore gets the same ID back, and the email is sent once. The check-and-insert is atomic in the repository, so two retries that arrive at the same moment cannot both get through. Two details:
- An omitted delivery time is fingerprinted as "asap", not as the time it resolves to. Otherwise, two retries a second apart would look different.
- An identical request after the window is treated as a deliberate new notification. An example is the same "password reset" email sent the next day.

**Provider failures.** A provider exception or failure result never stops a batch. It only affects that notification:
- A transient failure (an exception, or `TransientFailure`) sets the status back to `Pending` and increments `AttemptCount`. The notification is retried on the next run.
- After `MaxSendAttempts` attempts (3 by default), or after a `PermanentFailure` such as a rejected recipient, the status becomes `Failed`.

`LastError` and `LastAttemptAt` are stored so the failure can be investigated. If processing is cancelled during a send, the claimed notification goes back to `Pending` instead of being stuck in `Processing`.

**Error handling.** Validation errors return `400` with a `ValidationProblemDetails` that lists each field's errors. Malformed JSON and unparseable dates are rejected by ASP.NET's model binding, also with `400`. An unknown ID returns `404` as `ProblemDetails`. Unhandled exceptions return a generic `500` `ProblemDetails`, so stack traces are never sent to the client.

## 3. Assumptions

- **Subject is optional.** The spec does not list it as required. It is limited to 200 characters.
- **Priority is optional and defaults to `Normal`.** If it is supplied, it must be `Low`, `Normal` or `High`, in any letter case. Numeric strings such as `"2"` are rejected. `Enum.TryParse` would normally accept them.
- **Requested delivery time is optional.** If it is omitted, the notification is sent as soon as possible. A time in the future schedules the notification: it is not processed until that time.
- **"Not in the past" allows 30 seconds of tolerance.** A delivery time equal to the current time is valid. A time up to 30 seconds earlier is also accepted. Otherwise, a client that sends "now" would be rejected because of network latency or a slightly wrong clock. Times are compared as instants, so the timezone offset does not matter.
- **Email validation is a practical check, not full RFC 5322.** The address must be a single bare address with a dotted domain. Display-name forms such as `Name <a@b.com>` are rejected. Only actually sending an email proves the address exists.
- **"Processing pending notifications" is triggered by an endpoint.** This keeps it deterministic and easy to demo and test. See the improvements section.
- **Customer IDs are matched exactly** (case-sensitive) after trimming whitespace.
- Leading and trailing whitespace is trimmed from customer ID, recipient and subject.

## 4. Trade-offs

- **In-memory storage.** There is no setup and the tests are fast, but data is lost on restart. One lock around the store makes the atomic operations simple and correct. It would become a bottleneck under load, which does not matter at this scale.
- **A content hash instead of an idempotency key.** It works with callers as they are today, with no changes on their side. The downside is that two intentionally identical sends within 10 minutes are merged into one. I think that is the right default for notifications, but an `Idempotency-Key` header would be more precise (see below).
- **At-least-once delivery.** If the process crashed after the provider accepted an email but before the status was saved as `Sent`, the email could be sent again. Exactly-once delivery to an external system needs the provider to deduplicate. Real providers usually do this with a message ID, and we could pass the notification ID as that ID.
- **Sequential processing.** It keeps the priority order strict and is gentle on the provider. The cost is throughput.
- **No retry backoff.** A failed notification is retried on the next run, not after a delay.
- **Hand-written validator instead of FluentValidation.** There are only a few rules, so a dependency was not worth adding.

## 5. Production improvements

- **Persistence:** use a real database, such as SQL Server or Postgres. Add a unique index on `(Fingerprint, window)` or an idempotency-key table. Claim rows with `UPDATE … WHERE Status='Pending'` or `SELECT … FOR UPDATE SKIP LOCKED`. Add indexes on `(Status, Priority, RequestedDeliveryTime)` and `CustomerId`.
- **Background processing:** use a hosted `BackgroundService`, or a queue such as Service Bus or SQS, instead of a manual endpoint. Add a "lease timeout" so notifications stuck in `Processing` after a crash are picked up again.
- **Retries:** use exponential backoff with jitter (a `NextAttemptAt` column). Add a circuit breaker around the provider, for example with Polly. Move permanently failed notifications to a dead-letter queue, and add an endpoint to retry `Failed` notifications by hand.
- **Idempotency:** accept an `Idempotency-Key` header, and keep fingerprinting as a fallback.
- **Security:** add authentication and authorization. Only internal services should create notifications, and callers should only see their own customers. Add rate limiting.
- **API:** paginate the customer list. Add OpenAPI documentation and API versioning.
- **Observability:** use structured logs with PII masking, since recipient and message are personal data. Add metrics for sent, failed and queue depth, and add tracing and health checks.
- **Data retention:** purge old notifications, because message content may be sensitive.

## 6. Unclear requirements and how I interpreted them

| Requirement | Interpretation |
|---|---|
| "Exact same request … within a short period" | All content fields are identical (recipient compared case-insensitively) within 10 minutes, configurable. The duplicate is acknowledged and returns the original. It is not treated as an error, because the caller is retrying, not misbehaving. |
| What a duplicate should return | `200` with the original resource, instead of `409 Conflict`. A retrying client then gets the same answer it would have received the first time. |
| Status after a provider failure | Back to `Pending` for a retry, and `Failed` only after the retries run out or the error is permanent. The status values given in the spec cover this without adding a new one such as "Retrying". |
| Delivery time exactly equal to now | Valid, and the notification is due immediately. A small tolerance also covers clock skew. |
| Role of requested delivery time in processing | It schedules the notification: it is not sent before that time. Priority decides the order among notifications that are already due. |
| "High-priority processed before Normal and Low" | This applies within each processing run. A Low notification that is already due is not held back waiting for future High notifications. |
| Unsupported notification types (e.g. "Sms") | Rejected by validation with a list of the supported types. The processor also fails a notification safely if no provider is registered for its type. |

## Testing approach

The tests focus on the business rules, where mistakes would be costly:

- **`NotificationProcessorTests`**: priority order, a deterministic order for equal priorities (even with identical timestamps), scheduled delivery, a provider exception not stopping the batch, transient retries ending in `Failed`, permanent failures not being retried, sent notifications never being resent, and concurrent processing runs never sending the same notification twice.
- **`DuplicateDetectionTests`**: duplicates inside and outside the window, each field breaking duplicate matching when it changes, recipient letter case, retries with no delivery time, a retry after the notification was already sent, and 50 identical requests at the same time storing exactly one notification.
- **`CreateNotificationRequestValidatorTests`**: each rule, including invalid emails, numeric enum strings, "exactly now", the tolerance boundary and timezone offsets.
- **`NotificationsApiTests`**: integration tests through `WebApplicationFactory`. They check the HTTP contract: status codes, the `Location` header, `ProblemDetails` shapes, malformed JSON and the duplicate header.

A fake `TimeProvider` makes all time-dependent behaviour deterministic. I did not write tests for trivial code such as the DTO mapping or the simulated provider's logging.
