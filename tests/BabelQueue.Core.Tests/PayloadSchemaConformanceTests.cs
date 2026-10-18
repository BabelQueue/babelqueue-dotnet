using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BabelQueue.Schema;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// Runs the shared cross-SDK payload-schema cases (ADR-0024) from the vendored conformance
/// suite: this validator must agree with the Go, PHP, Python, Node and Java ones on each
/// case's <c>valid</c> flag.
/// </summary>
public class PayloadSchemaConformanceTests
{
    private static string SuitePath(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "conformance", relative);

    [Fact]
    public void PayloadCasesMatchAcrossSdks()
    {
        var manifest = SchemaJson.Parse(File.ReadAllText(SuitePath("manifest.json")))
            as IReadOnlyDictionary<string, object?>;
        Assert.NotNull(manifest);

        Assert.True(manifest!.TryGetValue("payload_schema", out var sectionObj));
        var section = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(sectionObj);
        var schema = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(section["schema"]);
        var cases = Assert.IsAssignableFrom<IReadOnlyList<object?>>(section["cases"]);
        Assert.NotEmpty(cases);

        var violations = new List<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(SuitePath("manifest.json")));
        foreach (var testCase in doc.RootElement.GetProperty("payload_schema").GetProperty("cases").EnumerateArray())
        {
            violations.AddRange(ConformanceRunner.PayloadSchemaCase(schema, testCase));
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
