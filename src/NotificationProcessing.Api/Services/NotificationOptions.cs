namespace NotificationProcessing.Api.Services;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>An identical request received within this window of the original is treated as a retry.</summary>
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How far in the past a requested delivery time may be before it is rejected. Absorbs clock skew and
    /// network latency so a client asking for "now" is not rejected because its request took 200ms to arrive.
    /// </summary>
    public TimeSpan DeliveryTimeTolerance { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Total send attempts (including the first) before a transiently failing notification is marked Failed.</summary>
    public int MaxSendAttempts { get; set; } = 3;

    /// <summary>Maximum notifications handled by a single processing run.</summary>
    public int ProcessingBatchSize { get; set; } = 100;
}
