# BabelQueue for .NET

[![CI](https://github.com/BabelQueue/babelqueue-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/BabelQueue/babelqueue-dotnet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/BabelQueue.Core.svg)](https://www.nuget.org/packages/BabelQueue.Core)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

> **Polyglot Queues, Simplified.** Read and write the canonical BabelQueue message
> envelope from .NET — so your C#/.NET services exchange messages with Laravel,
> Symfony, Python, Go, Node and Java over one strict JSON format, on the broker you
> already run.

This is the framework-agnostic **.NET core**: the wire-envelope codec, contracts
and dead-letter helpers — **zero dependencies** (in-box `System.Text.Json` only).
The full standard is documented at **[babelqueue.com](https://babelqueue.com)**.

## Installation

```bash
dotnet add package BabelQueue.Core
```

Targets **.NET 8**.

## Usage

```csharp
using BabelQueue;

// Produce — build the canonical envelope and publish the JSON to your broker.
var env = EnvelopeCodec.Make(
    "urn:babel:orders:created",
    new Dictionary<string, object?> { ["order_id"] = 1042L },
    queue: "orders");
string body = EnvelopeCodec.Encode(env); // compact UTF-8 JSON
// await db.ListRightPushAsync("queues:orders", body);
//   /  channel.BasicPublish("", "orders", props, Encoding.UTF8.GetBytes(body));

// Consume — decode a message produced by ANY BabelQueue SDK.
var incoming = EnvelopeCodec.Decode(body);
if (EnvelopeCodec.Accepts(incoming))
{
    switch (EnvelopeCodec.Urn(incoming))
    {
        case "urn:babel:orders:created":
            Console.WriteLine($"{incoming.Data!["order_id"]} {incoming.TraceId}");
            break;
    }
}
```

The envelope is identical to every other SDK's:

```json
{
  "job": "urn:babel:orders:created",
  "trace_id": "…",
  "data": { "order_id": 1042 },
  "meta": { "id": "…", "queue": "orders", "lang": "dotnet", "schema_version": 1, "created_at": 1749132727000 },
  "attempts": 0
}
```

> JSON numbers decode into `Data` as `long` (integers) or `double` (decimals);
> objects as `Dictionary<string, object?>` (insertion order preserved). `Encode`
> uses `UnsafeRelaxedJsonEscaping`, so slashes and non-ASCII stay literal and the
> bytes match the PHP/Python/Node/Java cores.

### Typed messages (optional)

```csharp
public sealed class OrderCreated(long orderId) : IPolyglotMessage, IHasTraceId
{
    public string GetBabelUrn() => "urn:babel:orders:created";
    public IReadOnlyDictionary<string, object?> ToPayload() =>
        new Dictionary<string, object?> { ["order_id"] = orderId };
    public string? GetBabelTraceId() => null; // or an inbound trace to continue
}

var env = EnvelopeCodec.FromMessage(new OrderCreated(1042L), "orders");
```

### Dead-letter

```csharp
var dlq = DeadLetters.Annotate(env, "failed", "orders", attempts: 3, error: "boom");
// publish EnvelopeCodec.Encode(dlq) to the "orders.dlq" queue
```

`DeadLetters.Annotate` returns a copy — the original envelope is preserved
unchanged inside the dead-lettered message, so any-language consumers can still
read it.

## What this core is (and isn't)

It enforces the **contract**: the envelope shape, URN identity, trace propagation,
schema-version gating and the dead-letter block. It is intentionally **not** a
worker/runtime — broker wiring, acks and retry loops stay in your own code (or a
future adapter), exactly as with the other SDK cores.

`UnknownUrnStrategy` (`Fail`, `Delete`, `Release`, `DeadLetter`) is provided for
adapters to act on.

## Conformance

This core passes the shared **cross-SDK conformance suite** (vendored under
`tests/BabelQueue.Core.Tests/conformance/`) — the same fixtures every BabelQueue
SDK must satisfy, so a .NET producer and, say, a Laravel consumer agree
byte-for-byte.

```bash
dotnet test
```

## License

[MIT](LICENSE) © Muhammet Şafak
