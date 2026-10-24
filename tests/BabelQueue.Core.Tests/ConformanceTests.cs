using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BabelQueue;
using BabelQueue.Schema;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Runs the shared cross-SDK conformance suite (vendored under <c>conformance/</c>,
/// copied to the test output) against this core — the same fixtures every BabelQueue
/// SDK must satisfy. Per-message fields (meta.id, trace_id, meta.created_at) are
/// intrinsically unique and are checked for presence only.
/// </summary>
public class ConformanceTests
{
    private static string SuitePath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "conformance", relative);

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return new object[] { c.GetProperty("name").GetString()! };
        }
    }

    [Fact]
    public void ManifestMatchesCoreSchemaVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        Assert.Equal(EnvelopeCodec.SchemaVersion, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.NotEmpty(doc.RootElement.GetProperty("cases").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Conformance(string caseName)
    {
        var violations = ManifestCase(FindCase(caseName));
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>Checks one top-level <c>cases</c> entry (decode + accepts + expected fields).</summary>
    internal static List<string> ManifestCase(JsonElement c)
    {
        var violations = new List<string>();
        var name = c.GetProperty("name").GetString();
        var body = File.ReadAllText(SuitePath(c.GetProperty("file").GetString()!));
        var env = EnvelopeCodec.Decode(body);

        void Expect(bool condition, string message)
        {
            if (!condition)
            {
                violations.Add($"{name}: {message}");
            }
        }

        if (!c.GetProperty("valid").GetBoolean())
        {
            var reason = c.TryGetProperty("reason", out var r) ? r.GetString() : "";
            Expect(!EnvelopeCodec.Accepts(env), $"invalid fixture must be rejected ({reason})");
            return violations;
        }

        Expect(EnvelopeCodec.Accepts(env), "valid fixture must be accepted");

        var expect = c.GetProperty("expect");
        Expect(expect.GetProperty("urn").GetString() == EnvelopeCodec.Urn(env), "urn mismatch");
        Expect(expect.GetProperty("attempts").GetInt32() == env.Attempts, "attempts mismatch");
        Expect(expect.GetProperty("lang").GetString() == env.Meta?.Lang, "meta.lang mismatch");
        Expect(expect.GetProperty("schema_version").GetInt32() == env.Meta?.SchemaVersion, "meta.schema_version mismatch");

        if (expect.TryGetProperty("data", out var dataElement))
        {
            Expect(JsonValueEquals(env.Data, ToValue(dataElement)), "data mismatch");
        }

        Expect(!string.IsNullOrEmpty(env.TraceId), "trace_id must be present");
        Expect(!string.IsNullOrEmpty(env.Meta?.Id), "meta.id must be present");
        Expect(env.Meta?.CreatedAt != 0, "meta.created_at must be present");

        if (expect.TryGetProperty("dead_letter", out var dlElement))
        {
            Expect(env.DeadLetter is not null, "dead_letter block missing");
            if (env.DeadLetter is not null)
            {
                if (dlElement.TryGetProperty("reason", out var reason))
                {
                    Expect(reason.GetString() == env.DeadLetter.Reason, "dead_letter.reason mismatch");
                }
                if (dlElement.TryGetProperty("original_queue", out var originalQueue))
                {
                    Expect(originalQueue.GetString() == env.DeadLetter.OriginalQueue, "dead_letter.original_queue mismatch");
                }
            }
        }

        return violations;
    }

    private static JsonElement FindCase(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (c.GetProperty("name").GetString() == name)
            {
                return c.Clone();
            }
        }
        throw new InvalidOperationException($"Conformance case not found: {name}");
    }

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static bool JsonValueEquals(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a is IReadOnlyDictionary<string, object?> da && b is IReadOnlyDictionary<string, object?> db)
        {
            if (da.Count != db.Count)
            {
                return false;
            }
            foreach (var kv in da)
            {
                if (!db.TryGetValue(kv.Key, out var bv) || !JsonValueEquals(kv.Value, bv))
                {
                    return false;
                }
            }
            return true;
        }
        if (a is System.Collections.IList la && b is System.Collections.IList lb)
        {
            if (la.Count != lb.Count)
            {
                return false;
            }
            for (var i = 0; i < la.Count; i++)
            {
                if (!JsonValueEquals(la[i], lb[i]))
                {
                    return false;
                }
            }
            return true;
        }
        return a.Equals(b);
    }
}

/// <summary>
/// Writes the machine-readable conformance result (ADR-0032 section 2) when
/// <c>BQ_CONFORMANCE_RESULTS</c> names a file, and always asserts that nothing failed.
/// </summary>
public class ConformanceResultsTests
{
    [Fact]
    public void EverySectionPassesAndResultsAreWrittenOnRequest()
    {
        var results = ConformanceResults.Run();

        var failures = results.Sections
            .SelectMany(s => s.Value.Select(c => (Section: s.Key, Case: c.Key, Result: c.Value)))
            .Where(x => x.Result.Status == "fail")
            .Select(x => $"{x.Section}/{x.Case}: {x.Result.Reason}")
            .ToList();

        var path = Environment.GetEnvironmentVariable("BQ_CONFORMANCE_RESULTS");
        if (!string.IsNullOrWhiteSpace(path))
        {
            File.WriteAllText(path, results.ToJson());
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ResultsCoverEveryInScopeSectionOfTheManifest()
    {
        var results = ConformanceResults.Run();
        foreach (var section in ConformanceResults.InScopeSections(results.Bindings))
        {
            Assert.True(results.Sections.ContainsKey(section), $"in-scope section '{section}' has no results");
            Assert.NotEmpty(results.Sections[section]);
        }
        // sections of a binding this package does not carry are absent, not skipped; derived from the manifest
        var absentSections = ConformanceResults.AbsentSections(results.Bindings).ToList();
        Assert.NotEmpty(absentSections);
        foreach (var absent in absentSections)
        {
            Assert.False(results.Sections.ContainsKey(absent), $"'{absent}' must be absent from a core-only package");
        }
    }

    [Theory]
    [InlineData("1.11.0+d388833894ae9ca34e7c51a778c54e29fedabf46", "1.11.0")]
    [InlineData("1.11.0", "1.11.0")]
    [InlineData("2.0.0-rc.1+abc", "2.0.0-rc.1")]
    public void PackageVersionDropsBuildMetadata(string informational, string expected)
    {
        Assert.Equal(expected, ConformanceResults.PackageVersion(informational));
    }

    [Fact]
    public void BindingsAreDerivedFromTheModulesTheCorePackageCarries()
    {
        Assert.Equal(new[] { "otel" }, ConformanceResults.DeriveBindings());
    }

    [Fact]
    public void ResultsRecordTheRepeatedKeySkipWithItsAuthorisingAdr()
    {
        var results = ConformanceResults.Run();
        var skipped = results.Sections["encode_reject"].Where(c => c.Value.Status == "skip").ToList();
        var only = Assert.Single(skipped);
        Assert.Equal("reject-repeated-key", only.Key);
        Assert.Contains("ADR-0031", only.Value.Reason);
    }

    [Fact]
    public void ResultsVersionComesFromTheAssemblyInformationalVersion()
    {
        using var doc = JsonDocument.Parse(ConformanceResults.Run().ToJson());
        var version = doc.RootElement.GetProperty("version").GetString();
        Assert.Equal(
            ConformanceResults.PackageVersion(typeof(EnvelopeCodec).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single().InformationalVersion),
            version);
        Assert.DoesNotContain("+", version);
        Assert.Matches(@"^[0-9]+\.[0-9]+\.[0-9]+$", doc.RootElement.GetProperty("conformance_version").GetString()!);
        Assert.Equal("babelqueue-dotnet", doc.RootElement.GetProperty("sdk").GetString());
    }
}

internal sealed record CaseOutcome(string Status, string? Reason);

internal sealed class ConformanceResults
{
    private const string Sdk = "babelqueue-dotnet";

    /// <summary>Manifest scopes this core package runs besides the bindings it carries.</summary>
    private const string CoreScope = "core";

    private static readonly Dictionary<string, Func<JsonElement, IEnumerable<(string Id, Func<CaseOutcome> Run)>>> Runners =
        new(StringComparer.Ordinal)
        {
            ["cases"] = section => Named("cases", section, c => Check(ConformanceTests.ManifestCase(c))),
            ["idempotency"] = IdempotencyCases,
            ["payload_schema"] = section => PayloadCases("payload_schema", section),
            ["payload_schema_unicode"] = section => PayloadCases("payload_schema_unicode", section),
            ["roundtrip"] = section => Named("roundtrip", section, c => Check(ConformanceRunner.Roundtrip(c, Fixture(c)))),
            ["data_shape"] = section => Named("data_shape", section, c => Check(ConformanceRunner.DataShape(c))),
            ["forbidden_keys"] = section => Named("forbidden_keys", section, c => Check(ConformanceRunner.ForbiddenKey(c, Fixture(c)))),
            ["extension_write"] = section => Named("extension_write", section, c => Check(ConformanceRunner.ExtensionWrite(c))),
            ["encode_reject"] = section => Named("encode_reject", section, EncodeRejectCase),
            ["trim"] = section => Named("trim", section, c => Check(ConformanceRunner.Trim(c, ReadFixture))),
            ["job_type"] = section => Named("job_type", section, c => Check(ConformanceRunner.JobType(c, ReadFixture))),
            ["traceparent"] = section => Named("traceparent", section, c => Check(ConformanceRunner.Traceparent(c))),
            ["retry_backoff"] = section => Named("retry_backoff", section, c => Check(ConformanceRunner.RetryBackoff(c))),
            ["poison_dlq"] = section => Named("poison_dlq", section, c => Check(ConformanceRunner.PoisonDlq(c, section.GetProperty("original_queue").GetString()!))),
        };

    public string ConformanceVersion { get; private init; } = string.Empty;

    public IReadOnlyList<string> Bindings { get; private init; } = Array.Empty<string>();

    public SortedDictionary<string, SortedDictionary<string, CaseOutcome>> Sections { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The bindings this package carries, derived from the code in the assembly under test, never listed
    /// by hand: <c>otel</c> when the W3C traceparent extract/inject code is in the core. The broker
    /// transports of the .NET family are separate packages with their own conformance runs.
    /// </summary>
    public static IReadOnlyList<string> DeriveBindings()
    {
        var bindings = new List<string>();
        if (typeof(EnvelopeCodec).Assembly.GetType("BabelQueue.Tracing.Traceparent") is not null)
        {
            bindings.Add("otel");
        }
        return bindings;
    }

    /// <summary>Sections the manifest scopes to <c>core</c> or to a binding in <paramref name="bindings"/>.</summary>
    public static IEnumerable<string> InScopeSections(IReadOnlyList<string> bindings)
    {
        using var doc = JsonDocument.Parse(ReadFixture("manifest.json"));
        return doc.RootElement.GetProperty("sections").EnumerateObject()
            .Where(s => IsInScope(s.Value.GetProperty("scope").GetString()!, bindings))
            .Select(s => s.Name)
            .ToList();
    }

    /// <summary>Sections the manifest scopes to a binding this package does not carry (ADR-0032 §2: absent, not skipped).</summary>
    public static IEnumerable<string> AbsentSections(IReadOnlyList<string> bindings)
    {
        using var doc = JsonDocument.Parse(ReadFixture("manifest.json"));
        return doc.RootElement.GetProperty("sections").EnumerateObject()
            .Where(s => !IsInScope(s.Value.GetProperty("scope").GetString()!, bindings))
            .Select(s => s.Name)
            .ToList();
    }

    public static ConformanceResults Run()
    {
        using var doc = JsonDocument.Parse(ReadFixture("manifest.json"));
        var bindings = DeriveBindings();
        var results = new ConformanceResults
        {
            ConformanceVersion = doc.RootElement.GetProperty("conformance_version").GetString()!,
            Bindings = bindings,
        };

        // The manifest `sections` map (not the top-level keys) says what exists and for which scope.
        foreach (var declared in doc.RootElement.GetProperty("sections").EnumerateObject())
        {
            if (!IsInScope(declared.Value.GetProperty("scope").GetString()!, bindings)
                || !Runners.TryGetValue(declared.Name, out var runner))
            {
                // out of scope: absent (not a skip). Unknown to this runner: left out too, so the
                // completeness gate reports it instead of this run guessing.
                continue;
            }

            var section = doc.RootElement.GetProperty(declared.Name);
            var outcomes = new SortedDictionary<string, CaseOutcome>(StringComparer.Ordinal);
            foreach (var (id, run) in runner(section))
            {
                CaseOutcome outcome;
                try
                {
                    outcome = run();
                }
                catch (Exception e)
                {
                    outcome = new CaseOutcome("fail", $"{e.GetType().Name}: {e.Message}");
                }
                outcomes[id] = outcome;
            }
            results.Sections[declared.Name] = outcomes;
        }

        return results;
    }

    public string ToJson()
    {
        var sections = Sections.ToDictionary(
            s => s.Key,
            s => s.Value.ToDictionary(
                c => c.Key,
                c => c.Value.Reason is null
                    ? new Dictionary<string, string> { ["status"] = c.Value.Status }
                    : new Dictionary<string, string> { ["status"] = c.Value.Status, ["reason"] = c.Value.Reason }));

        var informational = (System.Reflection.AssemblyInformationalVersionAttribute)typeof(EnvelopeCodec).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Single();

        return JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["sdk"] = Sdk,
                ["version"] = PackageVersion(informational.InformationalVersion),
                ["conformance_version"] = ConformanceVersion,
                ["generated_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                ["bindings"] = Bindings,
                ["sections"] = sections,
            },
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The package version without the SemVer build metadata (<c>+&lt;commit sha&gt;</c>) the SDK appends to
    /// the informational version, so it equals the published NuGet version.
    /// </summary>
    internal static string PackageVersion(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    private static bool IsInScope(string scope, IReadOnlyList<string> bindings) =>
        scope == CoreScope || bindings.Contains(scope);

    private static string ReadFixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", relative));

    private static string Fixture(JsonElement testCase) => ReadFixture(testCase.GetProperty("file").GetString()!);

    private static CaseOutcome Check(List<string> violations) =>
        violations.Count == 0 ? new CaseOutcome("pass", null) : new CaseOutcome("fail", string.Join("; ", violations));

    /// <summary>A section's <c>cases</c> array: one case per element, by <c>name</c>, else <c>&lt;section&gt;#&lt;index&gt;</c>.</summary>
    private static IEnumerable<(string Id, Func<CaseOutcome> Run)> Named(
        string sectionName, JsonElement section, Func<JsonElement, CaseOutcome> run)
    {
        var index = 0;
        foreach (var testCase in section.EnumerateArrayOrCases())
        {
            var c = testCase;
            var id = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()!
                : $"{sectionName}#{index}";
            index++;
            yield return (id, () => run(c));
        }
    }

    private static IEnumerable<(string Id, Func<CaseOutcome> Run)> PayloadCases(string sectionName, JsonElement section)
    {
        var schema = SchemaJson.Parse(section.GetProperty("schema").GetRawText()) as IReadOnlyDictionary<string, object?>
            ?? throw new InvalidOperationException($"{sectionName}: schema is not an object");
        return Named(sectionName, section, c => Check(ConformanceRunner.PayloadSchemaCase(schema, c)));
    }

    private static IEnumerable<(string Id, Func<CaseOutcome> Run)> IdempotencyCases(JsonElement section)
    {
        var dedupKey = section.GetProperty("dedup_key");
        yield return ("dedup_key", () => Check(ConformanceRunner.IdempotencyDedupKey(dedupKey, ReadFixture)));

        // sequences are named: the case id is `sequences/<name>`
        foreach (var (id, run) in Named("sequences", section.GetProperty("sequences"),
                     c => Check(ConformanceRunner.IdempotencySequence(c))))
        {
            yield return ($"sequences/{id}", run);
        }
    }

    private static CaseOutcome EncodeRejectCase(JsonElement testCase)
    {
        if (testCase.TryGetProperty("meta_entries", out _))
        {
            // A repeated key needs an ordered list of pairs; Make takes one dictionary per call, which
            // cannot repeat a key, so the manifest lets such an SDK skip the case (ADR-0031).
            return new CaseOutcome(
                "skip",
                "ADR-0031: meta_entries expresses a repeated key, which the one-dictionary-per-call Make(meta:) API cannot carry");
        }
        return Check(ConformanceRunner.EncodeReject(testCase));
    }
}

internal static class JsonElementCases
{
    /// <summary>The elements of the section's <c>cases</c> array, or of the element itself when it is an array.</summary>
    public static IEnumerable<JsonElement> EnumerateArrayOrCases(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
            : element.GetProperty("cases").EnumerateArray();
}
