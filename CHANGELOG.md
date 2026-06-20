# Changelog

All notable changes to `BabelQueue.Core` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The envelope wire format is versioned separately by `meta.schema_version`
(currently **1**) — see the contract at [babelqueue.com](https://babelqueue.com).

## [Unreleased]

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
