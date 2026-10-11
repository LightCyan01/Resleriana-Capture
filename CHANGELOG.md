# Changelog

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
