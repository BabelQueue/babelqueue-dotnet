namespace BabelQueue.Schema;

/// <summary>
/// A registration-time warning that a schema uses a keyword outside the supported Draft-07 subset
/// (ADR-0024). The keyword is ignored when validating, so the verdict is that of a schema without
/// it; the warning only tells the operator the schema asks for more than this SDK enforces.
/// </summary>
/// <param name="Location">
/// The RFC 6901 JSON Pointer, within the schema document, of the schema object that carries the
/// keyword (the empty string is the root).
/// </param>
/// <param name="Keyword">The unsupported keyword.</param>
public readonly record struct SchemaWarning(string Location, string Keyword);

/// <summary>
/// A <see cref="SchemaWarning"/> together with the URN whose registered schema produced it, so an
/// operator can tell which schema asks for more than the validator enforces.
/// </summary>
/// <param name="Urn">The message URN the schema is registered under.</param>
/// <param name="Warning">The unsupported-keyword warning (<c>Location</c> and <c>Keyword</c>).</param>
public readonly record struct RegisteredSchemaWarning(string Urn, SchemaWarning Warning)
{
    /// <summary>A human-readable line for logs; the conformance suite compares only the warning's location and keyword.</summary>
    public string Message =>
        $"BabelQueue: schema for '{Urn}': keyword '{Warning.Keyword}' at "
        + $"'{(Warning.Location.Length == 0 ? "<root>" : Warning.Location)}' "
        + "is not supported by the payload validator and is ignored";
}
