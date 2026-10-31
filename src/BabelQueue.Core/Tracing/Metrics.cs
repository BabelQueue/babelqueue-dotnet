using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace BabelQueue.Tracing;

/// <summary>
/// The standard BabelQueue metric set (ADR-0039) on <c>System.Diagnostics.Metrics</c>, the BCL
/// primitive the OpenTelemetry .NET SDK consumes — so the core takes <b>no</b> OpenTelemetry
/// package dependency (GR-7).
/// </summary>
/// <remarks>
/// <para>
/// The instruments live on one static <see cref="Meter"/> named <see cref="MeterName"/> and always
/// exist; a listener collects them only when it subscribes (for example
/// <c>builder.AddMeter("BabelQueue")</c>), so an application that wires nothing sees no change.
/// They are recorded by <see cref="Telemetry.Wrap(Handler, System.Collections.Generic.IReadOnlyDictionary{string, string}?, string?, string?)"/>
/// (<see cref="ProcessDurationName"/>, <see cref="ConsumedMessagesName"/>, <see cref="RetriesName"/>),
/// <see cref="Telemetry.PublishAsync{TResult}(string, System.Collections.Generic.IReadOnlyDictionary{string, object?}?, System.Collections.Generic.IDictionary{string, string}?, System.Func{Envelope, System.Threading.Tasks.Task{TResult}}, string, string?)"/>
/// (<see cref="SentMessagesName"/>) and <see cref="MetricsDeadLetterObserver"/> (<see cref="DlqMessagesName"/>).
/// </para>
/// <para>
/// Every point carries only <c>messaging.system</c>, <c>messaging.operation.name</c>,
/// <c>messaging.destination.name</c> (when known) and <c>error.type</c> (on failure, where the table of
/// ADR-0039 section 2 allows it). The URN, message id, trace id, attempts, schema version and the
/// dead-letter reason are never metric attributes: they are unbounded across a fleet.
/// </para>
/// <para>
/// The advisory bucket boundaries of <see cref="ProcessDurationName"/> (<see cref="AdvisoryBucketBoundaries"/>)
/// cannot be attached on <c>net8.0</c>, whose <c>Meter.CreateHistogram</c> takes no advice (it arrives
/// in .NET 9); a host configures the view with these values, otherwise the reader's defaults apply.
/// </para>
/// </remarks>
public static class TelemetryMetrics
{
    /// <summary>The meter name; add it to a provider with <c>AddMeter("BabelQueue")</c>.</summary>
    public const string MeterName = "BabelQueue";

    /// <summary>Histogram, unit <c>s</c>: the handler's wall time, once per handler invocation, success or failure.</summary>
    public const string ProcessDurationName = "messaging.process.duration";

    /// <summary>Counter, unit <c>{message}</c>: +1 per publish attempt, success or failure.</summary>
    public const string SentMessagesName = "messaging.client.sent.messages";

    /// <summary>Counter, unit <c>{message}</c>: +1 per delivery that enters the consumer wrapper.</summary>
    public const string ConsumedMessagesName = "messaging.client.consumed.messages";

    /// <summary>Counter, unit <c>{message}</c>: +1 per delivery that enters the wrapper with <c>attempts &gt; 0</c>.</summary>
    public const string RetriesName = "babelqueue.retries";

    /// <summary>Counter, unit <c>{message}</c>: +1 per dead-letter the SDK completed (via <see cref="MetricsDeadLetterObserver"/>).</summary>
    public const string DlqMessagesName = "babelqueue.dlq.messages";

    /// <summary>
    /// The semconv advisory bucket boundaries for <see cref="ProcessDurationName"/>, in seconds. A read-only
    /// list: a host cannot change the values another host reads.
    /// </summary>
    public static IReadOnlyList<double> AdvisoryBucketBoundaries { get; } =
        Array.AsReadOnly(new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 });

    internal static readonly string LibraryVersion = ResolveLibraryVersion();

    private static readonly Meter BabelMeter = new(MeterName, LibraryVersion);

    /// <summary><c>messaging.process.duration</c>.</summary>
    internal static readonly Histogram<double> ProcessDuration =
        BabelMeter.CreateHistogram<double>(ProcessDurationName, "s", "Duration of the consume handler");

    /// <summary><c>messaging.client.sent.messages</c>.</summary>
    internal static readonly Counter<long> SentMessages =
        BabelMeter.CreateCounter<long>(SentMessagesName, "{message}", "Messages the producer attempted to send");

    /// <summary><c>messaging.client.consumed.messages</c>.</summary>
    internal static readonly Counter<long> ConsumedMessages =
        BabelMeter.CreateCounter<long>(ConsumedMessagesName, "{message}", "Messages delivered to the consumer wrapper");

    /// <summary><c>babelqueue.retries</c>.</summary>
    internal static readonly Counter<long> Retries =
        BabelMeter.CreateCounter<long>(RetriesName, "{message}", "Deliveries that had already been attempted");

    /// <summary><c>babelqueue.dlq.messages</c>.</summary>
    internal static readonly Counter<long> DlqMessages =
        BabelMeter.CreateCounter<long>(DlqMessagesName, "{message}", "Dead-letters the SDK completed");

    internal static void RecordDelivery(string system, string? destination, int attempts)
    {
        if (ConsumedMessages.Enabled)
        {
            ConsumedMessages.Add(1, Tags(system, "process", destination, null));
        }

        if (attempts > 0 && Retries.Enabled)
        {
            Retries.Add(1, Tags(system, "process", destination, null));
        }
    }

    internal static void RecordProcessDuration(string system, string? destination, double seconds, string? errorType)
    {
        if (ProcessDuration.Enabled)
        {
            ProcessDuration.Record(seconds, Tags(system, "process", destination, errorType));
        }
    }

    internal static void RecordSent(string system, string? destination, string? errorType)
    {
        if (SentMessages.Enabled)
        {
            SentMessages.Add(1, Tags(system, "send", destination, errorType));
        }
    }

    internal static void RecordDeadLetter(string system, string? destination)
    {
        if (DlqMessages.Enabled)
        {
            DlqMessages.Add(1, Tags(system, "process", destination, null));
        }
    }

    private static TagList Tags(string system, string operationName, string? destination, string? errorType)
    {
        var tags = new TagList
        {
            { "messaging.system", system },
            { "messaging.operation.name", operationName },
        };
        if (Telemetry.HasDestination(destination))
        {
            tags.Add("messaging.destination.name", destination);
        }

        if (errorType is not null)
        {
            tags.Add("error.type", errorType);
        }

        return tags;
    }

    private static string ResolveLibraryVersion()
    {
        string? informational = typeof(TelemetryMetrics).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

/// <summary>
/// A <see cref="IDeadLetterObserver"/> that increments <c>babelqueue.dlq.messages</c> (ADR-0039
/// section 5.1) with <c>messaging.system</c>, <c>messaging.destination.name = queue</c> (when known)
/// and <c>messaging.operation.name = process</c>. The dead-letter reason and the DLQ name are not
/// metric attributes. Register it on a transport consumer that accepts an
/// <see cref="IDeadLetterObserver"/>.
/// </summary>
public sealed class MetricsDeadLetterObserver : IDeadLetterObserver
{
    private readonly string _system;

    /// <summary>Creates the observer.</summary>
    /// <param name="system">
    /// The <c>messaging.system</c> value (see <see cref="Telemetry.Systems"/>); absent or empty keeps the
    /// deprecated default <see cref="Telemetry.DefaultSystem"/>.
    /// </param>
    public MetricsDeadLetterObserver(string? system = null)
    {
        _system = string.IsNullOrEmpty(system) ? Telemetry.DefaultSystem : system;
    }

    /// <inheritdoc />
    public void OnDeadLetter(DeadLetterEvent deadLetterEvent)
    {
        ArgumentNullException.ThrowIfNull(deadLetterEvent);
        TelemetryMetrics.RecordDeadLetter(_system, deadLetterEvent.Queue);
    }
}
