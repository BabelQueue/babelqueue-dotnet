# Changelog

All notable changes to `BabelQueue.Core` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The envelope wire format is versioned separately by `meta.schema_version`
(currently **1**) — see the contract at [babelqueue.com](https://babelqueue.com).

## [Unreleased]

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

[Unreleased]: https://github.com/BabelQueue/babelqueue-dotnet/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/BabelQueue/babelqueue-dotnet/releases/tag/v0.1.0
