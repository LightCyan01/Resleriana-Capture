# Changelog

## 1.4.0

- Rewrite the recorder around focused API, Battle/Atelier, storage, archive, diagnostics and runtime modules, reusing the previous capture code and evidence.
- Preserve original API body bytes, observed headers, decoded fields and unknown fields without redaction. Add explicit request, response, operation and native-object lineage with honest missing or ambiguous origins.
- Add qualified Battle and Atelier native observations through a finite Windows x64 forwarding layer that preserves the original receiver, arguments, return values and native exceptions. Verify client/runtime identities before enabling native hooks; retain explicit blocked hooks and read failures.
- Replace repeated object graphs with gameplay projections, stored values, lifetime references and local contract tables. Remove repeated UI/render/controller history while retaining observed command inputs and outcomes for every skill, effect and recipe ID.
- Save admitted records and alternating checkpoints durably before acknowledgement. Hold on failed storage until recovery or explicit abort, retain provisional tails, and verify committed prefixes during recovery.
- Keep one five-entry ZIP per run/environment, the existing JSON Lines and compression model, and timestamp filenames with numeric collision suffixes. Remove ongoing Process-folder mirroring, encode each detached record once and clear current working data only after exact ZIP verification.
- Add readable per-capture, per-stream, per-system and per-route request/record counts, independent recounting, hashes and explicit completeness gaps. No encryption, automatic eligibility decisions or upload is added.
- Aggregate repeated warnings and two exact Unity material null-reference patterns using first full details, durable repeat records and summaries. Preserve other errors and the original console behavior outside the verified warning aggregation path.
- Move the package to embedded .NET 10.0.13 with a pinned 10.0.402 SDK, modified BepInEx/Il2CppInterop, HarmonyX 2.16.1, locked dependencies and runtime compatibility certificates. Keep English localization and patch its confirmed untouched-CAB preservation failure.
- Add focused storage, archive, network, native ABI, diagnostics, JSON and package checks, CSharpier/Prettier formatting, and one manually dispatched Windows verification job. Local builds record the current Git commit alongside exact source bytes.
- Record remaining native read/origin gaps, the external Atelier async null-reference error and pending HDD/SSD performance qualification; passing saved-data checks does not certify every native completion or owner lifetime.

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
