# Changelog

All notable changes to `BabelQueue.Core` are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The envelope wire format is versioned separately by `meta.schema_version`
(currently **1**) — see the contract at [babelqueue.com](https://babelqueue.com).

## [Unreleased]

Brings the opt-in payload validator (ADR-0024, `BabelQueue.Schema`) to parity with the other SDKs, as locked by the
`payload_schema_keywords` section of conformance 1.10.0. The envelope stays at `schema_version: 1`; nothing changes on
the wire and no package dependency is added (GR-7).

### Added
- Union `type`: `"type": ["string", "null"]` is valid when the value matches ANY listed type, and the keywords of the
  matching type (`minLength`, `minimum`, `properties`, ...) still apply. `integer` accepts every whole-valued number
  (`1.0`, `1e2`, `1.00m`).
- `maxLength` (counted in Unicode code points, like `minLength`).
- Registration-time warnings for keywords outside the supported subset: `PayloadValidator.Warnings(schema)` returns one
  `SchemaWarning(Location, Keyword)` per occurrence (`Location` is the RFC 6901 pointer of the schema object carrying the
  keyword), and `MapProvider` reports them when the schemas are registered, each as a `RegisteredSchemaWarning(Urn, Warning)` so
  the operator can tell which URN's schema carries it, through an `Action<RegisteredSchemaWarning>` hook
  (`new MapProvider(schemas, onWarning)` / `MapProvider.FromJson(raw, onWarning)`; `Trace.TraceWarning` by default) and
  keeps them in `MapProvider.Warnings`. Annotation keywords (`$schema`, `$id`, `title`, `description`, `examples`,
  `default`, `$comment`, `x-*`) never warn.

### Fixed
- `const` / `enum` compare numbers by mathematical value, recursively inside arrays and objects: `1`, `1.0` and `1e0` are
  equal and `2.5` equals `2.50` whatever the numeric type (`long`, `int`, `double`, `decimal`, `BigInteger`, ...). A
  boolean or a string is still never equal to a number. Previously a `long` const never matched a `double` value.
- `minLength` is enforced whatever numeric type the schema carries. It was only read when it was a `long`, so
  `"minLength": 2.0` (a `double`, or any other numeric type) was silently ignored; such a schema now rejects strings
  shorter than the limit, as the Java, Node and Python validators do. Schemas that carry an integer `minLength` are
  unaffected.
- A fractional `minLength` / `maxLength` is truncated toward zero (`2.5` is `2`) before the comparison, like the Go, PHP,
  Python and Java validators, so `{"type":"string","minLength":2.5}` accepts `"ab"` on every SDK. A non-finite bound
  (`NaN`, `±Infinity`; only reachable from a hand-built schema) is ignored.

### Changed
- Vendored conformance suite bumped to 1.10.0 (new `payload_schema_keywords` section, run by the conformance tests and
  recorded in `results.json`).

## [1.14.0] - 2026-11-01

Implements ADR-0038 (OpenTelemetry semantic-conventions alignment) and ADR-0039 (standard metrics) for the
`System.Diagnostics` tracing helpers in `BabelQueue.Core`, and ADR-0036 decision 4 (body classification) with the
additive `BodyVerdict` API. The envelope stays at `schema_version: 1`; nothing on the wire changes and no package
dependency is added (GR-7).

### Changed (dashboard-breaking, ADR-0038 section 5)
- Span names are now `send <queue>` (producer) and `process <queue>` (consumer); they were `publish <urn>` and
  `process <urn>`. With no destination (a blank `meta.queue`) the name is the bare `send` / `process`. The URN is
  never in the span name; it is the `babelqueue.urn` attribute.
- The producer span's `messaging.destination.name` is the queue it publishes to; it was the URN. It is not written
  when the queue is blank.
- `messaging.system` is the new optional `system` argument (for example `"kafka"`, `"rabbitmq"`, `"aws_sqs"`,
  `"servicebus"`, `"pulsar"`, `"activemq"`, `"redis"`; see `Telemetry.Systems`). **Omitting it keeps `babelqueue` and
  that default is deprecated** (it matches no APM messaging view). A value outside the set is written as given.
- A `null` queue given to `PublishAsync` is treated as `"default"`, as `EnvelopeCodec.Make` does, so the span's
  destination equals the produced `meta.queue`.
- Consumer attributes are set when the span starts (a sampler can read them); they used to be set afterwards.
- A failing publish or handler now also sets `error.type` (the exception's full type name, `_OTHER` if none) next to
  the existing error status and `exception` event. On a failing publish the envelope's `messaging.message.id` and
  `messaging.message.conversation_id` are on the span (they were set only after a successful send).

### Added
- `Telemetry.Wrap(handler, headers, system, consumerGroup)` and `Telemetry.PublishAsync(..., queue, system)`: optional
  `system` and `consumerGroup` (`messaging.consumer.group.name`, consumer span only, written whenever given). Existing
  signatures are unchanged in source and in binary: `Wrap(handler)`, `Wrap(handler, headers)` and both pre-existing
  `PublishAsync` shapes (with a required `queue`, hidden from IntelliSense) stay as their own methods and forward to the
  new overloads with `system: null`, so assemblies compiled against 1.13 keep binding to them and no call shape is
  ambiguous.
- New span attributes: `messaging.operation.type`, `messaging.operation.name`, `babelqueue.urn`,
  `babelqueue.schema_version`, `babelqueue.attempts`; the producer also writes `messaging.message.conversation_id`.
- `Telemetry.SemconvVersion` (`1.44.0`) and `Telemetry.SchemaUrl` (`https://opentelemetry.io/schemas/1.44.0`). The
  `net8.0` `ActivitySource` and `Meter` cannot carry a schema URL, so the core only exposes the constant; the source
  and meter are created with the library version.
- Metrics (ADR-0039) on `Meter("BabelQueue")` (`TelemetryMetrics`): `messaging.process.duration` (histogram, `s`),
  `messaging.client.sent.messages`, `messaging.client.consumed.messages`, `babelqueue.retries` and
  `babelqueue.dlq.messages` (counters, `{message}`). Attributes are only `messaging.system`,
  `messaging.operation.name`, `messaging.destination.name` (when known) and `error.type` (on failure); no URN, id,
  trace id, attempts or reason. Only the metric-name constants and the read-only `AdvisoryBucketBoundaries` are public;
  the instruments themselves are internal, so nothing outside the SDK can record on them. The instruments always exist and a listener collects them only when it subscribes
  (`AddMeter("BabelQueue")`). `net8.0` cannot attach the advisory bucket boundaries, so they are exposed as
  `TelemetryMetrics.AdvisoryBucketBoundaries` for a view.
- Dead-letter observer seam (ADR-0039 section 5.1): `IDeadLetterObserver`, `DeadLetterEvent(Queue, DeadLetterQueue,
  Reason)` and `DeadLetterObservers.NotifySafely` (catches and logs an observer exception so it cannot change the
  settle outcome), plus `MetricsDeadLetterObserver(system)` which increments `babelqueue.dlq.messages`. The core has no
  consume runtime or DLQ write point, so nothing calls the observer yet; the transport packages wire it in a later
  release.
- A `trace_id` that is not a UUID is still mapped by hashing (unchanged); the consumer helper now logs a warning
  through `System.Diagnostics.Trace`, once per distinct value, and preserves the value byte for byte
  (message-envelope.md section 5.1).
- The vendored conformance suite is now v1.9.0. The runner covers the `span-shape`, `trace-id` and `metrics` sections
  and the core-scope `identity_verdict` section added in v1.7.0 (97 cases, all passing).
- `BodyVerdict.Classify(string?)` (ADR-0036 decision 4, ADR-0032 section 6): a strict, never-throwing classifier that
  returns a `BodyVerdictResult` (`Kind` of `BodyVerdictKind.Ok` or `Poison`, plus a `Cause`). A body is `Ok` when it
  decodes into an envelope with a valid identity; it is `Poison` when it is not one JSON object, when a known member
  has the wrong type (for example `attempts: "3"` or `data: []`), when it nests deeper than `BodyVerdict.CoreMaxDepth`
  (64), or when neither `job` nor `urn` is a non-blank string. It lets a consumer park a malformed body on the poison
  route. `BodyVerdictKind.Poison` is the zero value, so `default(BodyVerdictResult)` is poison, never `Ok`.
  **`EnvelopeCodec.Decode` is unchanged**: it stays lenient about member types, with the same signatures and behaviour.
- The nesting limit (64) is .NET-specific: Go and Node accept 10000 levels, PHP 511, Java 256, Python depends on the
  interpreter (ADR-0036 decision 4, "SDK note").
- Documented platform deviation (owner decision 2026-10-31; recorded in ADR-0036 decision 4, "SDK note"): a body holding a lone UTF-16 surrogate escape (`"\ud800"`) anywhere is `Poison` here,
  because System.Text.Json cannot materialise it and `EnvelopeCodec.Decode` throws `InvalidOperationException` on it
  (so an `Ok` body is always one `Decode` returns for). The Go, Python and Node classifiers accept such a body; the
  conformance suite has no `identity_verdict` case for it.

### Deprecated
- Removed in the next MINOR: the dual-emitted span attributes `messaging.operation` and
  `messaging.babelqueue.attempts`; use `messaging.operation.type` / `messaging.operation.name` and
  `babelqueue.attempts`.
- Leaving `system` out (the `babelqueue` default) is deprecated. What an absent value means once the deprecation window
  ends is not decided yet (ADR-0038 section 5); it is not scheduled for removal.

## [1.13.0] - 2026-10-24

Implements ADR-0034 section 9 (retry backoff). The envelope stays at `schema_version: 1`.

### Added
- `PoisonRecord.Build` now validates that `deadLetterJson` is a single JSON object (`ArgumentNullException` / `ArgumentException` otherwise); regression tests added.
- `RetryBackoffPolicy` / `RetryJitter`: the shared worker-side retry backoff
  `d(n) = min(RetryBackoffMax, RetryBackoff * RetryBackoffMultiplier^(n-1))` (seconds, `n = attempts + 1`,
  `RetryBackoffMax = 0` = no cap) with `none` (default) or `full` (`uniform(0, d(n))`) jitter. The defaults
  yield a delay of `0`, so nothing changes until a transport configures it. Kafka does not apply it (its
  retry tier sets the delay).
- `PoisonRecord.Build(body, deadLetterJson)`: the DLQ record of a poison body per ADR-0035 (a strict JSON
  object is spliced byte-for-byte, anything else becomes `{"raw", "raw_encoding"?, "dead_letter"}` with lossless
  base64 for invalid UTF-8, never U+FFFD). The conformance runner covers the core-scope `poison_dlq` section.
- Conformance runner covers the v1.6.0 `retry_backoff` section; the vendored suite is now v1.6.0 and CI
  verifies `fetch.sh` against a pinned sha256 (`BQ_FETCH_SH_SHA256`) before running it.

## [1.12.0] - 2026-10-19

Implements ADR-0032 (conformance packaging, results format, ASCII trim and the non-string `job` rule).
The envelope stays at `schema_version: 1`.

### Changed
- `make`'s `trace_id` trim and the blank checks in `Accepts` and `Redrive` use the closed ASCII set (space,
  `\t`, `\n`, `\v`, `\f`, `\r`); Unicode whitespace that `Trim()` / `string.IsNullOrWhiteSpace` matched no
  longer counts (ADR-0032 section 5).
- Decode: a non-string `job` is ignored with a warning and a valid string `urn` takes over; a non-string
  `urn` needed as the fallback is ignored with its own warning (ADR-0032 section 6).

### Added
- Conformance runner covers the v1.5.0 sections and writes `results.json` (ADR-0032 section 2);
  `scripts/check-conformance-results.py` gates its completeness in CI, and the release attaches it as an
  asset. CI fetches and verifies the conformance v1.5.0 release asset.

## [1.11.0] - 2026-10-17

Implements ADR-0031 (producer `meta` write API, encode-time rejection, closed ASCII blank set).
The envelope stays frozen at `schema_version: 1`.

### Added
- `EnvelopeCodec.Make(urn, data, queue, traceId, meta)` (new overload, all parameters required): producer-defined
  `meta` entries, preserved on every re-emit. A key colliding with a core `meta` key (`id`, `queue`,
  `lang`, `schema_version`, `created_at`) or a forbidden one (`max_retries`, `attempts`, `source`,
  `ts`) throws `BabelQueueException`, as does a value that cannot be serialized to JSON (the
  serializer error is the inner exception). The four-parameter `Make(urn, data, queue, traceId)`
  is kept as a separate overload with its exact pre-1.11.0 signature, so assemblies compiled
  against earlier Core versions stay binary-compatible; the `meta` overload takes all five
  parameters.
- Decode warns (`'/urn'`) when `job` and `urn` arrive together: `job` is kept and `urn` is dropped.
- Tests: the conformance runners execute `extension_write` and `encode_reject` and the conformance
  1.4.0 optional fields (`expect_preserved`, `expect_absent_after_reencode`, `apply_dead_letter`,
  `expect_dead_letter`), with unit tests proving a violation is reported.

### Changed
- **Behaviour change.** `EnvelopeCodec.Encode` now throws `BabelQueueException` for a core or
  forbidden key found in `Extras` (root or `meta`), including `Extras` filled directly, instead of
  silently skipping it.
- **Behaviour change.** The blank-`job` check (fallback to `urn`, `Urn()`, `Make`) uses the closed
  ASCII set (space, `\t`, `\n`, `\v`, `\f`, `\r`) instead of Unicode whitespace, for cross-SDK parity.
  A `job` made only of non-ASCII whitespace (U+00A0, U+0085, U+2003, ...) carrying a valid `urn` no
  longer routes to the `urn` handler; it falls to the unknown-URN strategy (`fail` / `delete` /
  `release`). With `delete` it is removed silently. With `fail` and a DLQ, decode drops `urn`, so
  the DLQ copy loses its routable identity (ADR-0031 Consequences/Negative).

## [1.10.0] - 2026-10-12

### Added
- `Redrive.IRewinder`: optional `Redrive.ITransport` capability (the counterpart of Go's `Rewinder`).
  `RedriveAsync` calls `RewindAsync` exactly once whenever an exception ends a run early — a failed
  restore write back to the DLQ, a failed redrive publish (whether or not its restore succeeded), a
  failed acknowledgement/commit, or a throwing `Select` callback — so the rest of the drained batch
  (popped, never processed) is redelivered and a later cumulative commit (Kafka) cannot move the
  group past it. The message passed is the one being handled; an implementation must rewind to what
  is not yet committed, never past it. The original error is rethrown; if the rewind fails too, both
  are reported together as a flat `AggregateException` (a failed redrive publish whose restore also
  fails is reported in the same aggregate). A failed restore write never acknowledges the DLQ message
  and no longer masks the original error. Transports that do not implement `IRewinder` are
  unchanged. Additive — MINOR.

### Changed

- Tests only: the vendored conformance suite is now 1.3.0 (exact-case envelope keys, last-wins duplicates: new `invalid-wrong-case-job`, `wrong-case-keys-roundtrip` and `duplicate-keys-last-wins` fixtures). No production change; the .NET core already behaved this way.

## [1.9.0] - 2026-10-11

### Added
- `Redrive.IKeyedRepublisher`: optional `Redrive.ITransport` capability so a DLQ redrive carries
  broker-native per-message metadata (the Kafka record key, contract §6.5) byte-for-byte on both the
  redrive write and the restore write back to the DLQ. Transports that do not implement it are
  unchanged (plain `PublishAsync`). Additive — MINOR.

## [1.8.0] - 2026-10-03

MINOR: adds public API (`Envelope.Extras`, `Meta.Extras`, `EnvelopeCodec.Decode(string, Action<string>?)`)
and changes re-emit behaviour (unknown keys are now preserved); no existing signature changed.

### Fixed
- **Unknown keys survive re-emit (forward compatibility, GR-5).** `Envelope` and `Meta` gain
  an init-only `[JsonExtensionData] Extras` (`Dictionary<string, JsonElement>?`, `null` when
  there are none). `EnvelopeCodec.Decode` captures every non-canonical top-level and `meta`
  key verbatim (raw JSON — `1.0` stays `1.0`, objects stay objects) and `Encode` writes them
  after the canonical fields, which keep their order. Every re-emit path carries them:
  retry (`with { Attempts = … }`), `DeadLetters.Annotate`, `Redrive.Reset` and the outbox.
  Previously such keys were silently dropped. Treat `Extras` as read-only: a `with` copy
  shares the dictionary with the original — assign a new dictionary to change it.
- **Forbidden keys (K-15).** `timestamp`, `meta.max_retries`, `meta.attempts`, `meta.source`
  and `meta.ts` are no longer accepted silently: decode still succeeds, but each one raises a
  warning naming its JSON pointer (e.g. `/meta/max_retries`) and is dropped — never captured
  as an extra, never re-emitted. New overload `EnvelopeCodec.Decode(string, Action<string>?)`
  receives the warnings; `Decode(string)` writes them to `System.Diagnostics.Trace`.
- **`minLength` counts Unicode code points** (`string.EnumerateRunes()`), not UTF-16 code
  units — an astral emoji is 1, matching the other SDK validators (ADR-0024).

### Tests
- The conformance runner now executes the `roundtrip`, `data_shape`, `forbidden_keys` and
  `payload_schema_unicode` manifest sections (none skipped); a list-typed `data` is rejected.

## [1.7.0] - 2026-06-21

### Added
- **Runtime GDPR field encryption (ADR-0030)** — the SDK-enforcement half of the
  `x-gdpr-sensitive` governance keyword: an opt-in, producer/consumer pair that encrypts
  exactly the `data` fields a schema declared sensitive, leaving the wire envelope frozen.
  - New `BabelQueue.Gdpr` namespace: the caller-provided `ICipher` interface
    (`string Encrypt(byte[])` / `byte[] Decrypt(string)`) — bound to a KMS / Vault / HSM /
    tokenisation service, so the **core pulls no crypto dependency (GR-7)** — plus a
    reference `AesGcmCipher` on the in-box `System.Security.Cryptography.AesGcm`
    (AES-256-GCM, random 12-byte nonce, 16-byte tag, base64; the key is the caller's, no
    key management). `Gdpr.Protect(data, schema, cipher)` / `Gdpr.Unprotect(...)` are
    standalone, opt-in helpers, and `ProtectedFieldException` is the typed wrong-key error.
  - **Sensitive paths come from the schema, not the message.** `SchemaSensitivity.SensitivePaths`
    extracts every `x-gdpr-sensitive` mark (boolean `true` or a non-empty string category) from a
    per-URN schema — nested objects (`profile.full_name`) and array items (`addresses[].line`)
    included. Parsing the keyword is **validation-neutral** (the payload validator ignores it), so
    annotating a schema is never a breaking change (GR-1).
  - **The envelope stays frozen (GR-1).** `Protect` rewrites only **values** inside `data`: a marked
    leaf's value is canonically JSON-encoded (the codec's compact, relaxed-escaping options) and
    replaced by the cipher's ciphertext **string**. It adds/renames/removes **no** envelope field,
    `meta.schema_version` stays **1**, `trace_id` is untouched (GR-4), and a ciphertext value is a
    JSON string so `data` stays **pure JSON** (GR-3) — an SDK without the key still carries the
    envelope. `Unprotect` is the byte-for-byte inverse (numbers restore to `long`/`double`, objects
    to `Dictionary`, matching `System.Text.Json`); an absent field is skipped, a non-string leaf is
    left untouched (idempotent), and a wrong key throws `ProtectedFieldException` so the message
    takes the retry / dead-letter path.
  - Strictly opt-in and backward compatible (GR-6). Validate **cleartext** — before `Protect` on
    the producer and after `Unprotect` on the consumer — because a schema constraining a sensitive
    field would otherwise reject the ciphertext string. (Note: the type and namespace are both
    `Gdpr`, so alias the type at the call site, e.g. `using GdprFields = BabelQueue.Gdpr.Gdpr;`.)

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

[Unreleased]: https://github.com/BabelQueue/babelqueue-dotnet/compare/v1.8.0...HEAD
[1.8.0]: https://github.com/BabelQueue/babelqueue-dotnet/compare/v1.7.0...v1.8.0
[1.0.0]: https://github.com/BabelQueue/babelqueue-dotnet/compare/v0.1.0...v1.0.0
[0.1.0]: https://github.com/BabelQueue/babelqueue-dotnet/releases/tag/v0.1.0
