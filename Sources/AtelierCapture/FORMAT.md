# Capture archive format

Schema 4 recordings use one ZIP with five flat entries: `api.jsonl`, `combat.jsonl`, `raw-bodies.jsonl`, `diagnostics.jsonl` and `manifest.json`. The JSONL files contain UTF-8 JSON objects, one per line. Empty API, combat or raw-body streams are valid.

`manifest_version` is 1 and `format` is `resleriana-capture`. Each manifest entry describes one extracted JSONL stream: `name`, `sha256`, `bytes` and `records`. Hash the exact extracted bytes, including line endings; `records` counts LF bytes. `manifest.json` does not hash itself. ZIP64 sizes must be supported for long recordings.

The manifest copies `schema`, `run_id`, `environment`, timestamps, stopwatch frequency, raw-data flag and producer `metadata` from the original diagnostic session records. Producer identity is self-reported. `capture_status` lists installation/reader results, skipped hooks, observed combat calls and observer failures. `observer_errors` uses cumulative totals when a final status and session end are available; otherwise it counts available warning records. These totals span both environments in the run. `observer_error_samples` counts sampled warnings separately. `dropped_events` totals writer-queue gaps.

`shutdown_complete` means the writer recorded a normal session end. It does not mean every gameplay action was captured. `recovered`, `checkpoint_verified`, `recovery_uncertain` and `discarded_compressed_bytes` distinguish recovery from normal shutdown. Older partial recordings without checkpoints are uncertain; their original temporary files are kept. Missing or corrupt schema 4 checkpoints prevent packaging, and the originals remain available.

During recording, `session-state.json` commits the run ID, environment, lengths and SHA-256 digests of completed compressed prefixes. It stays in the temporary directory and is not an upload entry. `checkpoint_sha256` hashes the exact checkpoint bytes; it is null when no checkpoint exists. Archive creation uses those lengths and checksums, then writes hashes of the extracted streams. Hashing and copying use fixed-size buffers.

If a completed ZIP remains beside a temporary directory, recovery retries cleanup only after matching the run, environment and checkpoint hash and verifying all four extracted stream hashes, sizes and record counts. Older ZIPs without this checkpoint binding retain their temporary files. Shutdown waits for requests already admitted to the capture writer to finish their enqueue or drop accounting before writing final status and gap records.

## Validation contract

An ingestion service must reject duplicate or unexpected ZIP names, directories/path traversal, encryption, unsupported compression or schemas, malformed UTF-8/JSONL, digest/size/count mismatches, conflicting session metadata, and files exceeding its resource limits. Validate ZIP structure and declared sizes before decompressing; enforce the same limits on actual output. Never extract uploads to paths supplied by the archive.

Run/environment metadata must agree across records. Event sequences are unique when present but need not be consecutive or sorted: threads enqueue concurrently, streams are separate, and one run can record two environments. Early combat records can have `request_id = 0`. Empty combat alone is not evidence of a broken recording. A public official-capture collector may reject `Private` archives as its own policy.

Diagnostic markers do not all have the event envelope. `session_start` is written lazily, so event timestamps can precede it. `session_end.events` counts queued envelope records across the four streams and excludes diagnostic markers. Do not infer corruption from timestamp order or from markers without an event sequence.

`raw-bodies.jsonl` preserves original entity bytes as Base64, with byte counts and SHA-256 digests. Unknown fields can be decoded later. Common sensitive fields are redacted in decoded logs, but the original raw bodies are retained. Limits apply to each body/snapshot, not to the length of a play session: bodies over 32 MiB are omitted explicitly, snapshots have a 12,000-node/10-level limit, collections stop at 4,096 items, and the writer queue holds at most 2,048 events/64 MiB. Unawaited async results are marked; hooked downstream consumers provide the resolved battle state.

## Trust boundary

`authenticity` is `unverified`. Hashes detect changed or damaged streams relative to a manifest. Someone who modifies the plugin can fabricate events, copy a release's reported hash, and recompute the manifest. Checksums, public source and GitHub release attestations cannot prove what ran on that user's computer or that gameplay occurred on the official server.

Enforce validation at a trusted ingestion boundary before accepted captures become research inputs. Browser checks improve feedback and protect the interface, but are bypassable. Independent official evidence or external attestation would be needed to establish stronger provenance; there is no secret signing key embedded in the plugin.
