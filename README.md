# Resleriana Capture

Record API traffic, Battle and Atelier observations from Atelier Resleriana (JP) on Steam. Includes English translation from [Localization 2.0.0](https://github.com/ongandrew/AtelierResleriana/releases/tag/2.0.0). For Windows x64, the verified JP 2.16.0 client with Unity 2022.3.62f2.

Version 1.4.0 rewrites the recorder with durable storage, focused Battle/Atelier observations, readable counts and stricter recovery checks. Captures retain the five-entry ZIP format and timestamp filenames.

Built on a modified BepInEx 6.0.0-be.788 with HarmonyX 2.16.1. Recorder observations use a finite Windows x64 native forwarding layer: the original game method runs on its original receiver and preserves its native exception handling. Managed callbacks copy data before and after that call.

The plugin records requests and responses inside the game, plus inputs, results and state at qualified native boundaries. Original native identities connect observations where they are available; missing or ambiguous identities remain explicit. The package embeds .NET 10.0.13, with pinned source and dependency identities. Each admitted gameplay and network callback waits for a durable data/checkpoint flush before continuing. Warning and known material-error diagnostics are queued without waiting, and the writer saves everything already queued in one flush.

## Install

1. Close the game. Open the [latest release](https://github.com/LightCyan01/Resleriana-Capture/releases/latest) and download `Resleriana-Capture-vX.Y.Z-win-x64.zip` under Assets.
2. Preserve the existing `dotnet` and `BepInEx/core` folders outside the game folder, then replace them with the complete folders in the ZIP. Copy the remaining package files beside `AtelierResleriana.exe`. Preserve captures, configuration, custom localization files and unrelated plugins. Install only one `AtelierCapture.dll`.
3. For an existing configuration, retain your settings and set `[IL2CPP] UnityBaseLibrariesSource = 2022.3.62.zip`. The default `GlobalMetadataPath = {GameDataPath}/il2cpp_data/Metadata/global-metadata.dat` and `IL2CPPInteropAssembliesPath = {BepInEx}` are required. In `AtelierResleriana.Plugin.Localization.cfg`, set `[Data] AutoUpdate = false` so its data stays consistent with the verified package; preserve language and other options. The package contains offline Unity references and English localization data.
4. Launch the game through Steam. Recording starts automatically. The first launch may take a few minutes to set up. A mismatching runtime/client or failed hook produces explicit incomplete evidence.

## Captures

After closing the game, find your capture here:

```text
BepInEx/plugins/Captures/Official/capture-YYYY-MM-DD_HH-mm-ss.zip
```

Each ZIP contains four JSON Lines text files and a manifest.

| File                | Information recorded                                                                                                                                                                                                                                                                     |
| ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `api.jsonl`         | Decoded requests and responses, routes, response codes and complete observed headers. Depending on what you play, this includes characters, parties, inventory, currencies, quest progress, gacha results, mail and rewards.                                                             |
| `combat.jsonl`      | Battle and Atelier arguments, results and focused gameplay state. Typed lifetime references identify owner links; mutable state is copied again at each observation. Required read, origin and completion gaps remain explicit. UI/render internals are excluded.                        |
| `raw-bodies.jsonl`  | Original API request and response bodies stored as Base64, with byte lengths and SHA-256 hashes. These preserve data that the decoded logs may omit.                                                                                                                                     |
| `diagnostics.jsonl` | Session identifiers, timestamps, request completion states, timings, errors and capture gaps. Identical warnings and two exact Unity material null-reference patterns retain their first full details plus durable repeat records and summaries. Other errors retain their full details. |
| `manifest.json`     | Readable per-ZIP, per-stream, per-system and per-route record/request counts, hashes, runtime/client identity and recording gaps. Entry/exit records, observed sends and gameplay operations have separate units. No count encryption or automatic giveaway score is produced.           |

Let the game finish closing so it can save the ZIP. Completed current working folders clear after their exact data is independently verified in the ZIP. Failed, provisional and older folders remain for recovery; leave them in place. If it crashes, launch it again to recover a saved recording. A capture covers the actions you played; limits or failures can leave gaps. Names use the timestamp above, with a numeric suffix such as `-2` when that name is already occupied.

Original bodies, decoded fields and observed headers retain their captured values, including account and session data. The recorder performs no redaction or identifier hashing. It saves locally and does not upload recordings.

## How recording works

The plugin observes the game's HTTP send, response decoder and completion paths. It preserves observed original API bodies before decoding, so unknown fields remain available; streaming and transport gaps are explicit. Combat hooks copy selected native state while it is valid, then queue detached records for background writing and compression. It avoids per-frame polling. A full queue waits for the writer instead of discarding events; faster compression during play helps keep it draining. Sustained slow storage can still cause waits.

Native snapshots carry a local type-description table, so field-availability descriptions are stored once per contract in that observation. Every mutable gameplay value is still read afresh. Historical captures with inline node descriptions remain readable; [FORMAT.md](Sources/AtelierCapture/FORMAT.md) describes both forms.

All touched complete gzip members and the alternating checkpoint pass durable flush before the callback is acknowledged. Observations already queued share that flush; warning and known material-error callbacks return before it, so a crash can lose occurrences still queued. Each detached record is encoded once. The `Process` working folder preserves startup observations before server attribution; its frozen contents join the first resolved environment once. It does not mirror ongoing battle writes. Unresolved native origins remain explicit diagnostic observations. On exit, the writer verifies every stream and independently recounts the ZIP; proven current working copies clear afterward. Recovery selects a verified committed prefix and retains provisional tails and unresolved sources. Older already-packaged folders are preserved without repeating their full verification at quit. No record is deliberately clipped, sampled or dropped to meet an upload limit. One ZIP covers one run and environment.

If storage is full or failing, capture holds until storage recovers or you explicitly abort it. While the game is running, use `capture-control.ps1 -ProcessId <game PID> -Command status`. An abort requires the exact current run ID and a reason: `capture-control.ps1 -ProcessId <game PID> -Command abort -RunId <run ID> -Reason <reason>`. Aborting marks the run incomplete and retains completed data; it cannot persist data to a broken drive.

The manifest checks file integrity. A modified plugin can fabricate data and recompute its hashes, so the manifest does not prove official gameplay. Package checksums and source/dependency manifests identify installer contents; they do not authenticate recordings. See [the archive format](https://github.com/LightCyan01/Resleriana-Capture/blob/main/Sources/AtelierCapture/FORMAT.md) for the validation contract and limits.

Native coverage varies by hook and field. Blocked hooks, failed reads and ambiguous object origins are reported as capture gaps. See [FORMAT.md](Sources/AtelierCapture/FORMAT.md) for the supported data and its limits.

## Build from source

Install these tools:

- [Git for Windows](https://git-scm.com/install/windows).
- [Python 3.11 or newer for Windows](https://www.python.org/downloads/windows/).
- Visual Studio 2022 C++ build tools with the Windows SDK for the native forwarding layer. Exact compiler identity and native build inputs are recorded with the package.
- The build downloads and verifies the pinned .NET 10.0.402 SDK and .NET 10.0.13 runtime into `work/toolchain`; a separate global SDK is unnecessary.

Open a new Command Prompt or PowerShell window in the folder where you want the source, then run:

```text
git clone https://github.com/LightCyan01/Resleriana-Capture.git
cd Resleriana-Capture
py scripts/build.py
```

The command builds the modified BepInEx runtime, HarmonySupport, native forwarding layer and capture plugin, then creates `work/release/Resleriana-Capture-vX.Y.Z-win-x64.zip`. The first build needs an internet connection for checksum-pinned public dependencies. A cached build can use `--offline`. No game installation or assemblies from another workspace are needed.

Extract the ZIP into the game folder as described under [Install](#install). To rebuild after editing the source, run `py scripts/build.py` again.

See [Sources](Sources/README.md) for individual project builds and release instructions.
