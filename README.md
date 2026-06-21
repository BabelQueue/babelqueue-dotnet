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

### Replay-bypass (optional)

A deliberate replay off the DLQ (`Redrive.RedriveAsync`) re-runs the handler,
re-firing its external side-effects — a second charge, a duplicate email. With
`Bypass`, redrive stamps a `bq-replay-bypass` **transport header** on each replayed
message; a handler reads the delivered headers and skips the effects that already
ran, while the idempotent core still runs (ADR-0027).

```csharp
// PRODUCER — redrive with bypass (the transport must be an IHeaderPublisher).
await Redrive.RedriveAsync(transport, "orders.dlq", new Redrive.Options(Bypass: true));

// CONSUMER — the adapter surfaces the delivered message's out-of-band headers.
await handler(env);                                          // idempotent core — always runs
await Replay.BypassExternalEffectsAsync(headers, async () => // skipped when Replay.IsReplay(headers)
{
    await SendConfirmationEmailAsync(env);
});
```

The marker rides **beside** the frozen envelope on the out-of-band header carrier,
never inside it (`schema_version` stays **1**, GR-1; `trace_id` preserved, GR-4) —
the same seam as the `traceparent` header. It takes effect only when the transport
implements `Redrive.IHeaderPublisher`; otherwise `Bypass` is a no-op (`Bypassed:
false`) and the message is still redriven.

### Transactional outbox (optional)

A plain producer does two things that must both happen or neither — commit the
business row **and** publish the message — across two systems, so a crash between
them loses or duplicates the message (the *dual write*). The outbox (ADR-0029)
removes it: the message is stored **into the same database, in the same
transaction** as the business data, then a separate **relay** publishes the
durable rows.

```csharp
using BabelQueue.Outbox;

// WRITE — the caller owns the transaction boundary (this is the whole point).
var outbox = new Outbox(store);                       // store : IOutboxStore (your DB, ADO.NET)
await using var tx = await db.BeginTransactionAsync(ct);
await db.InsertOrderAsync(order, tx, ct);             // the business write
var env = EnvelopeCodec.Make("urn:order:placed", data, "orders");
await outbox.WriteAsync(env, ct);                     // same connection, same tx — encodes & saves
await tx.CommitAsync(ct);                             // both, or neither

// RELAY — drain pending rows and publish them, on a worker loop / scheduler.
var relay = new OutboxRelay(
    (body, queue, c) => transport.PublishAsync(queue, body),   // your publish seam (verbatim body)
    store);
await relay.DrainAsync(cancellationToken: ct);        // FlushAsync() does one batch
```

`Outbox.WriteAsync` encodes via the frozen codec and delegates to
`IOutboxStore.SaveAsync` — it **never begins or commits** anything, so it composes
with your existing unit-of-work. The relay publishes the **stored bytes verbatim**
(never decodes/rebuilds the envelope), so `schema_version` stays **1** (GR-1/GR-5)
and `trace_id` is preserved end-to-end (GR-4). A publish that throws marks the row
failed and leaves it pending — one poison row never blocks the batch — with a
bounded, capped backoff; `DrainAsync` loops until no progress is made.

This is **exactly-once handoff**, not exactly-once delivery: after a crash the relay
re-publishes a row, so consumers must stay idempotent (`Idempotency.Wrap` is the
consumer-side mirror, ADR-0022). The core takes **no DB dependency (GR-7)** — bind
`IOutboxStore` to your own table over ADO.NET; the bundled `InMemoryOutboxStore` is
for tests / single-process demos and does not claim/lock rows (a production adapter's
job).

## What this core is (and isn't)

It enforces the **contract**: the envelope shape, URN identity, trace propagation,
schema-version gating and the dead-letter block. It is intentionally **not** a
worker/runtime — broker wiring, acks and retry loops stay in your own code (or a
future adapter), exactly as with the other SDK cores.

`UnknownUrnStrategy` (`Fail`, `Delete`, `Release`, `DeadLetter`) is provided for
adapters to act on.

## OpenTelemetry tracing (optional)

`BabelQueue.Tracing` adds **opt-in** OpenTelemetry tracing built only on the in-box
`System.Diagnostics.Activity` — the primitive the OpenTelemetry .NET SDK consumes —
so the core still takes **zero** package dependencies. To export, wire a
`TracerProvider` that listens to the source `Telemetry.ActivitySourceName`
(`"BabelQueue"`, e.g. `.AddSource("BabelQueue")`); with no listener the helpers are
nearly free and emit nothing. The wire envelope is never touched.

Cross-hop propagation works at two layered levels:

- **`trace_id` correlation (v0.1):** `Telemetry.Wrap`/`Telemetry.PublishAsync` map the
  envelope's `trace_id` to an OTel trace id, so every hop that shares a `trace_id`
  shares one trace — correlation and per-hop timing.
- **W3C `traceparent` span linkage (v0.2):** the header-aware overloads carry the
  active span across a hop as a W3C `traceparent` on an **out-of-band header carrier**
  that rides *beside* the frozen envelope (never inside it), so the consumer span
  becomes a true **child** of the producer span. No header ⇒ it falls back to the
  v0.1 `trace_id` parent — a strict, backward-compatible upgrade.

```csharp
using BabelQueue.Tracing;

// PRODUCER — inject the active span's traceparent into a carrier the adapter
// carries on the transport's metadata channel, beside the envelope.
var headers = new Dictionary<string, string>();
string id = await Telemetry.PublishAsync(
    "urn:babel:orders:created",
    new Dictionary<string, object?> { ["order_id"] = 1042L },
    headers,                                   // <- the out-of-band carrier
    env => myTransport.SendAsync(env, headers), // adapter carries `headers`
    queue: "orders");

// CONSUMER — pass the delivered message's headers; the process span is started
// as a child of the producer span (or the trace_id parent when absent).
Handler traced = Telemetry.Wrap(async env => { /* handle */ }, deliveredHeaders);
await traced(incoming);
```

> The carrier is `IDictionary<string,string>` to write (producer) /
> `IReadOnlyDictionary<string,string>` to read (consumer) — the .NET counterpart of
> the Go `HeaderPublisher`/`ReceivedMessage.Headers` and Node `HeaderCarrier` seams.
> `Traceparent.Inject` / `Traceparent.RemoteParentFromHeaders` expose the W3C
> inject/extract directly. **Per-adapter transport wiring** (the .NET transports live
> in the separate `BabelQueue.Sqs` / `BabelQueue.Redis` / `BabelQueue.MassTransit`
> packages) is a documented follow-up; this core ships the mechanism.

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
