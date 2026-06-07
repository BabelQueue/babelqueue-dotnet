using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using BabelQueue;
using Xunit;

namespace BabelQueue.Tests;

/// <summary>
/// GR-8 budget: the envelope encode/decode path must add no more than 2% over plain
/// JSON serialization (the baseline a publisher already pays), measured against a
/// conservative broker round-trip. Pure CPU — no broker — so the gate is stable and
/// environment-independent in CI. Same methodology + reference as every other SDK.
/// </summary>
public class OverheadBenchmarkTests
{
    // Conservative networked broker round-trip (ns): local loopback Redis measures
    // ~300µs; production brokers are slower, so 750µs is conservative.
    private const double ReferenceBrokerRoundTripNs = 750_000;

    private static Dictionary<string, object?> Data() => new()
    {
        ["order_id"] = 1042L,
        ["amount"] = 99.9,
        ["currency"] = "USD",
        ["note"] = "café ☕",
    };

    [Fact]
    public void CodecOverheadWithinBudget()
    {
        var data = Data();

        void Envelope() =>
            EnvelopeCodec.Decode(EnvelopeCodec.Encode(EnvelopeCodec.Make("urn:babel:orders:created", data)));

        void Bare()
        {
            var body = JsonSerializer.Serialize(data);
            _ = JsonSerializer.Deserialize<Dictionary<string, object?>>(body);
        }

        var marginal = Math.Max(0.0, NsPerOp(Envelope) - NsPerOp(Bare));
        var overhead = marginal / ReferenceBrokerRoundTripNs * 100;

        Assert.True(
            overhead <= 2.0,
            $"codec overhead {overhead:F2}% exceeds the 2% GR-8 budget (marginal {marginal:F0} ns)");
    }

    private static double NsPerOp(Action fn)
    {
        for (var i = 0; i < 50_000; i++)
        {
            fn(); // warm up (JIT)
        }

        const int iterations = 200_000;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            fn();
        }

        sw.Stop();
        return sw.Elapsed.TotalNanoseconds / iterations;
    }
}
