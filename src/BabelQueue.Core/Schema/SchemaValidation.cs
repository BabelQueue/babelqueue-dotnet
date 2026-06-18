using System.Collections.Generic;
using System.Threading.Tasks;
using BabelQueue;

namespace BabelQueue.Schema;

/// <summary>
/// Optional per-URN <c>data</c> schema validation for a babelqueue producer or consumer
/// (ADR-0024). A <see cref="ISchemaProvider"/> supplies a JSON Schema for a message URN —
/// typically built from a babelqueue-registry <c>registry.json</c> — and the message's
/// <c>data</c> is validated against it. It is opt-in: a URN with no registered schema is never
/// validated. Producer-side, call <see cref="Validate"/> before publishing (or <see cref="Check"/>
/// to branch without throwing); consumer-side, wrap a handler with <see cref="Wrap"/>. The .NET
/// mirror of the Go <c>schema.Check</c>/<c>schema.Wrap</c> helpers.
/// </summary>
public static class SchemaValidation
{
    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    /// <summary>
    /// The first <c>data</c> violation for <paramref name="urn"/>/<paramref name="data"/>, or
    /// <c>null</c> when valid or when no schema is registered for the URN (opt-in).
    /// </summary>
    public static string? Check(ISchemaProvider provider, string urn, IReadOnlyDictionary<string, object?> data)
    {
        var schema = provider.SchemaFor(urn);
        return schema is null ? null : PayloadValidator.Validate(schema, data);
    }

    /// <summary>
    /// Validate <paramref name="urn"/>/<paramref name="data"/> against its registered schema,
    /// throwing <see cref="InvalidPayloadException"/> otherwise. The producer-side guard.
    /// </summary>
    public static void Validate(ISchemaProvider provider, string urn, IReadOnlyDictionary<string, object?> data)
    {
        var violation = Check(provider, urn, data);
        if (violation is not null)
        {
            throw new InvalidPayloadException(urn, violation);
        }
    }

    /// <summary>
    /// Returns <paramref name="handler"/> wrapped to validate each message's <c>data</c>
    /// against its URN's schema before the handler runs (consumer-side safety net).
    /// </summary>
    public static Handler Wrap(ISchemaProvider provider, Handler handler) =>
        async envelope =>
        {
            Validate(provider, EnvelopeCodec.Urn(envelope), envelope.Data ?? Empty);
            await handler(envelope).ConfigureAwait(false);
        };
}
