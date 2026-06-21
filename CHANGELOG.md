# Changelog

All notable changes to `BabelQueue.Core` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The envelope wire format is versioned separately by `meta.schema_version`
(currently **1**) — see the contract at [babelqueue.com](https://babelqueue.com).

## [Unreleased]

## [1.6.0] - 2026-06-21

### Added
- **Transactional outbox helper (ADR-0029)** — an opt-in, producer-side fix for the
  *dual write*: persist the message **into the same database, in the same transaction**
  as the business data (so it commits or rolls back atomically with it), then a separate
  relay publishes the durable rows. No distributed transaction; exactly-once *handoff*
  into the broker, at-least-once on the wire as always.
  - New `BabelQueue.Outbox` namespace: `IOutboxStore` (the DB-agnostic persistence
    contract — `SaveAsync` / `FetchUnpublishedAsync` / `MarkPublishedAsync` /
    `MarkFailedAsync`, all async + `CancellationToken`), the `Outbox` writer
    (`WriteAsync(envelope, ct)`), `OutboxRelay` (`FlushAsync` / `DrainAsync`) over an
    injectable `OutboxPublisher` publish seam, the `OutboxRecord` / `OutboxRelayResult`
    records and a reference `InMemoryOutboxStore`.
  - **The caller owns the transaction boundary.** `Outbox.WriteAsync` encodes via the
    frozen codec and calls `IOutboxStore.SaveAsync` *inside the transaction the caller
    already opened* — it never begins/commits anything. The store binds to the caller's
    own DB over ADO.NET, so the core takes **zero new dependencies (GR-7)**.
  - **The relay publishes the stored bytes verbatim** — it never decodes, rebuilds or
    re-encodes the envelope, so the body that reaches the broker is byte-identical to
    what was stored (`schema_version` stays **1**, GR-1/GR-5; `trace_id` preserved, GR-4).
    A throwing publish marks the row failed and leaves it pending (one poison row never
    blocks the batch), with a bounded, linearly-growing, capped backoff (injectable async
    delay so tests stay instant). `DrainAsync` loops until a pass makes no progress, with
    a hard safety ceiling.
  - Fully opt-in and backward compatible (GR-6); a production deployment binds
    `IOutboxStore` to a real DB table, the in-memory store is for tests / single-process
    demos. Per the ADR, relay claim/lock (so two relays don't double-publish a row) is
    the adapter's concern; the in-memory reference does not implement it.

## [1.5.0] - 2026-06-21

### Added
- **Replay-bypass — an out-of-band side-effect guard for DLQ replay (ADR-0027).** A
  deliberate `Redrive.RedriveAsync` re-runs the handler and re-fires its external
  side-effects (a second charge, a duplicate email); `Idempotency.Wrap` stops an
  *accidental* duplicate, not the *intended* reprocess. This closes that gap.
  - New `Redrive.Options(Bypass: true)` stamps a `bq-replay-bypass` **transport
    header** on each redriven message; `Redrive.Item` gains a `Bypassed` flag. It takes
    effect only when the transport implements the new optional
    `Redrive.IHeaderPublisher` (`PublishWithHeadersAsync(queue, body, headers)`) —
    otherwise `Bypass` is a no-op and the message is still redriven (`Bypassed:
    false`), exactly like the Go reference.
  - New `Replay.IsReplay(headers)` + `Replay.BypassExternalEffectsAsync(headers,
    effect)` consume-side guard (plus the `Replay.HeaderReplayBypass` constant): a
    handler wraps its external, non-idempotent side so a replay skips it while the
    idempotent core still runs.
  - The marker rides **beside** the frozen envelope on the out-of-band header carrier
    (`IReadOnlyDictionary<string,string>` to read), never inside it (`schema_version`
    stays **1**, GR-1; `trace_id` preserved, GR-4) — the same seam as the `traceparent`
    header. **Zero new dependencies (GR-7).** Fully opt-in and backward compatible: a
    header-less message behaves exactly as before. Per-adapter transport wiring
    (`BabelQueue.Sqs` / `BabelQueue.Redis` / `BabelQueue.MassTransit`) is the documented
    follow-up, like ADR-0028's.

## [1.4.0] - 2026-06-21

### Added
- **W3C `traceparent` transport-header propagation (ADR-0028, OTel v0.2)** — true
  cross-hop **span** parent-child linkage layered over the v0.1 `trace_id`
  correlation (ADR-0025). New `BabelQueue.Tracing.Traceparent` exposes the W3C
  inject/extract — `Inject(headers, activity?)` writes the active `Activity`'s span
  context as a `traceparent` (and `tracestate`) onto an out-of-band header carrier;
  `RemoteParentFromHeaders(headers)` parses a delivered `traceparent` into a remote
  `ActivityContext`; `Format`/`Parse` implement the frozen W3C format directly. New
  header-aware overloads:
  `Telemetry.PublishAsync(urn, data, IDictionary<string,string> headers, send, queue)`
  injects the producer span's `traceparent` into the carrier (and still stamps
  `trace_id`), and `Telemetry.Wrap(handler, IReadOnlyDictionary<string,string> headers)`
  starts the consumer span as a **child** of the producer span when the delivered
  message carries a valid `traceparent` — else it falls back to the v0.1
  `trace_id`-derived parent (no regression). Opt-in.
  - The carrier (`IDictionary<string,string>` to write / `IReadOnlyDictionary<…>` to
    read) is the .NET counterpart of the Go `HeaderPublisher`/`ReceivedMessage.Headers`
    and Node `HeaderCarrier` seams: out-of-band metadata that rides **beside** the
    frozen envelope, never inside it.
  - **Zero new dependencies (GR-7):** built only on the in-box
    `System.Diagnostics.Activity`/`ActivityContext`/`ActivityTraceId` — the W3C
    parse/format is implemented against the frozen format, no propagator library.
    The wire envelope is untouched (`schema_version` stays `1`, GR-1) and `trace_id`
    is preserved (GR-4).
  - **Per-adapter transport wiring** (carrying the header on each transport's native
    metadata channel — `BabelQueue.Sqs` / `BabelQueue.Redis` / `BabelQueue.MassTransit`)
    is a documented follow-up; this core ships the mechanism.

## [1.0.0] - 2026-06-07

**1.0.0 — the public API is now SemVer-stable**: breaking changes require a MAJOR,
following the deprecation policy. The wire envelope is unchanged
(`schema_version: 1`). Full reference at [babelqueue.com](https://babelqueue.com).

### Internal
- CI enforces **Roslyn analyzers** (`AnalysisLevel=latest-recommended`, warnings as
  errors) and a **coverlet line-coverage gate** (`/p:Threshold=90`). Fixed CA1859
  (concrete return type for a private codec helper) surfaced by the analyzers.
- **GR-8 latency benchmark** (`OverheadBenchmarkTests`) — asserts the envelope
  encode/decode path adds **≤2%** over plain-JSON serialization vs a conservative
  750µs broker round-trip.

## [0.1.0] - 2026-06-06

### Added
- `EnvelopeCodec` — builds (`Make`, `FromMessage`), encodes and decodes the
  canonical `{job, trace_id, data, meta, attempts}` envelope (`schema_version` 1).
  The single .NET implementation of the wire format.
- `Envelope` / `Meta` / `DeadLetter` immutable `record` types.
- `EnvelopeCodec.Encode` emits compact UTF-8 JSON (slashes/unicode unescaped, via
  `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`) — byte-identical to the PHP,
  Python, Node and Java cores (insertion order preserved).
- `EnvelopeCodec.Urn(...)` — resolve the URN (`job`, accepting `urn` as an alias).
- `EnvelopeCodec.Accepts(...)` — consumer-side validation (rejects empty URN,
  unsupported `meta.schema_version`, missing `data`, blank `trace_id`).
- `DeadLetters.Annotate(...)` — additive `dead_letter` block builder.
- Contracts `IPolyglotMessage` / `IHasTraceId`.
- `UnknownUrnStrategy` (`Fail` / `Delete` / `Release` / `DeadLetter`);
  `BabelQueueException` / `UnknownUrnException`.
- Shared cross-SDK **conformance suite** under `tests/.../conformance/` (vendored
  from the canonical `conformance/` set) plus a runner.

### Notes
- Pre-1.0: the public API may change before the `1.0.0` tag.
- **Zero runtime dependencies** (in-box `System.Text.Json`); targets **.NET 8**.

[Unreleased]: https://github.com/BabelQueue/babelqueue-dotnet/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/BabelQueue/babelqueue-dotnet/compare/v0.1.0...v1.0.0
[0.1.0]: https://github.com/BabelQueue/babelqueue-dotnet/releases/tag/v0.1.0
