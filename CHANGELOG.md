# Changelog

## 1.4.2

- Fix privacy mode discarding native Battle and Atelier gameplay inputs, results and state. Retain qualified values and references while filtering personal data.
- Preserve filtered gameplay from native protobuf messages, including messages whose original snapshot contained only protobuf bytes.
- Retain public probability strings, asset references, store product identifiers and typed mail-template values and times.
- Preserve unknown numeric protocol enum values and report omitted fields with reasons and occurrence counts.
- Identify the corrected filtering policy separately and recover older privacy captures without changing their recorded policy.

## 1.4.1

- Add an optional privacy configuration, disabled by default, that removes credentials, personal identifiers and profile details while retaining qualified gameplay progress.
- Omit original bodies, headers, free-form logs and unaudited native values in privacy mode, with explicit recording gaps and per-run privacy metadata.
- Preserve original recording policy during recovery; privacy mode leaves older full recordings unchanged.
- Document account and session fields, configuration, reduced capture coverage and account-import limitations.

## 1.4.0

- Rewrite the recorder with improved API, Battle and Atelier capture.
- Preserve original API bodies, headers and unknown fields, with clearer links between requests, responses and gameplay observations.
- Replace repeated object graphs with focused gameplay snapshots and references, preserving observed inputs and results while excluding UI and rendering internals.
- Preserve supported gameplay records without the previous body, collection and depth truncation limits; report unsupported reads and hooks as explicit gaps.
- Reduce allocation, native type lookup and checkpoint overhead during recording.
- Queue warnings and known Unity material errors without waiting on disk, and save queued records together. A crash can lose diagnostics that have not been saved; API and gameplay records still wait for durable saving.
- Speed up archive parsing and hashing, and compress ZIP entries on a dedicated worker without depending on game callback threads.
- Add durable saving, stronger crash recovery and verified archive cleanup; fix buffer growth and network-lock failure paths.
- Add readable request and record counts for each capture, stream, system and route.
- Aggregate repeated warnings and known Unity material null-reference patterns while retaining their details and occurrence counts.
- Capture Unity messages and exception stacks from background threads as well as the main thread.
- Describe gameplay and queued diagnostic durability separately in capture manifests.
- Add capture status and explicit abort commands through `capture-control.ps1`.
- Check client and runtime compatibility before enabling native capture.
- Upgrade the embedded runtime to .NET 10.0.13 and adapt BepInEx/Il2CppInterop for the new runtime and native capture.
- Fix asset-bundle preservation in the English localization companion.

Captures remain one ZIP per run and environment, with the same five entries, compression model and timestamp filenames. Schema 5 records the new evidence and durability metadata; schema 4 recordings remain readable during recovery.

## 1.3.3

- Read native generic enumerable battle targets through their iterator interfaces, preserving values previously lost to collection-reader errors.
- Check populated and empty native enumerables during startup before enabling combat capture.
- Include native exception details when startup reader checks fail.
- Keep BepInEx and Il2CppInterop build versions separate from the capture release version, including in CI environments.

## 1.3.2

- Preserve events when the writer queue fills by waiting for space instead of discarding them.
- Use faster gzip compression during play; the finished ZIP keeps its existing compression.
- Allow one record larger than the retained-data budget to drain on its own, and release waiting callers if output fails.
- Serialize capture construction before copying bodies or snapshots so concurrent waiting callbacks do not accumulate capture data outside the queue budget.

## 1.3.1

- Build the runtime, capture plugin and install ZIP with `py scripts/build.py`; version and source commit are read automatically.
- Reuse the build directory on subsequent builds instead of failing when it already exists.
- Shorten the Windows build instructions and use the same command in Command Prompt and PowerShell.

## 1.3.0

- Add `manifest.json` to capture ZIPs with stream SHA-256 hashes, byte and record counts, schema/plugin identity, hook coverage and recording gaps.
- Checkpoint completed compressed blocks so crash recovery verifies saved data and reports unfinished tails. Preserve uncertain legacy recordings.
- Recover older recordings separately from the active writer and handle recordings started in the same second without overwriting them.
- Finish admitted events before closing the writer, verify completed ZIPs before retrying temporary-file cleanup, and count scalar values against the snapshot limit.
- Record skipped combat hooks, reader and observer failures, transport exception types and retry counts in capture diagnostics.
- Capture summon-result records and battle fields previously omitted by the 96-field limit. Keep the existing queue, depth, collection and body limits.
- Publish AtelierCapture source and remove installer/runtime binaries from the Git tree. Build releases from public source and checksum-pinned dependencies, without game-generated assemblies.
- Build and package the modified BepInEx 6.0.0-be.788 runtime, HarmonyX 2.16.1 and custom HarmonySupport with English Localization 2.0.0.
- Publish installer checksums, a release manifest and GitHub build provenance. Capture manifests describe integrity, not proof of genuine gameplay.

Download `Resleriana-Capture-v1.3.0-win-x64.zip` and extract its contents beside `AtelierResleriana.exe`. Existing captures are preserved. New captures remain one ZIP per run, with four JSONL streams and a small manifest.
