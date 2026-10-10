# Resleriana Capture

Record API traffic and combat data from Atelier Resleriana (JP) on Steam. Includes English translation from [Localization 2.0.0](https://github.com/ongandrew/AtelierResleriana/releases/tag/2.0.0). For Windows x64, game version 2.16.0.

Built on a modified BepInEx 6.0.0-be.788 with HarmonyX 2.16.1. The custom hook fixes are in Il2CppInterop's HarmonySupport, where they handle the game's Windows x64 value-type and nullable arguments.

The plugin records requests and responses inside the game, plus inputs, results and state before and after hooked combat methods. Request IDs connect battle events to API traffic. Keeping both decoded data and original bodies lets researchers inspect gameplay now and decode more fields later. Log writing and compression run on a background thread.

## Install

1. Close the game. Open the [latest release](https://github.com/LightCyan01/Resleriana-Capture/releases/latest) and download `Resleriana-Capture-vX.Y.Z-win-x64.zip` under Assets.
2. Extract the ZIP and copy its contents into the game folder, beside `AtelierResleriana.exe`.
3. Launch the game through Steam. Recording starts automatically. The first launch may take a few minutes to set up.

## Captures

After closing the game, find your capture here:

```text
BepInEx/plugins/Captures/Official/capture-YYYY-MM-DD_HH-mm-ss.zip
```

Each ZIP contains four JSON Lines text files and a manifest.

| File | Information recorded |
| --- | --- |
| `api.jsonl` | Decoded requests and responses, routes, response codes and selected headers. Depending on what you play, this includes characters, parties, inventory, currencies, quest progress, gacha results, mail and rewards. |
| `combat.jsonl` | Hooked battle methods and their inputs/results, unit stats, HP, damage, break, buffs/debuffs, gauges, targets and turn order. |
| `raw-bodies.jsonl` | Original API request and response bodies stored as Base64, with byte lengths and SHA-256 hashes. These preserve data that the decoded logs may omit. |
| `diagnostics.jsonl` | Session identifiers, timestamps, request completion states, timings, errors and capture gaps. |
| `manifest.json` | Stream hashes, sizes and record counts, plugin/schema versions, installed and used combat hooks, recording gaps and recovery status. |

Let the game finish closing so it can save the ZIP. If it crashes, launch it again to recover the saved part of the recording. Leave any `.partial` folders in place until then. A capture covers the actions you played; limits or failures can leave gaps.

Raw bodies keep the original bytes, including any account or session fields in them. The decoded logs redact common sensitive fields. The plugin saves locally and does not upload recordings.

## How recording works

The plugin observes the game's HTTP send, response decoder and completion paths. It preserves the original API bodies before decoding, so unknown fields remain available. Combat hooks copy selected native state while it is valid, then queue detached records for background writing and compression. It avoids per-frame polling and keeps its queue bounded; any dropped events are reported.

Completed gzip blocks are checkpointed during play. On exit, the writer streams them into a ZIP and calculates the manifest hashes. Recovery uses those checkpoints to exclude an unfinished crash tail. Older recordings without checkpoints are marked uncertain and their temporary files are retained.

The manifest checks file integrity. A modified plugin can fabricate data and recompute its hashes, so the manifest does not prove official gameplay. Release checksums and GitHub build attestations identify the published installer; they do not authenticate recordings. See [the archive format](Sources/AtelierCapture/FORMAT.md) for the validation contract and limits.

## Build from source

Install Git, Python 3.11 or later, and the .NET 8 SDK. Clone this repository, then run from its root in PowerShell:

```powershell
py scripts/build.py --workdir ../capture-build --version 1.3.0 --source-commit (git rev-parse HEAD)
py scripts/package.py --stage ../capture-build/stage --output ../capture-release
```

The build downloads checksum-pinned public dependencies, builds the modified BepInEx runtime and HarmonySupport, and compiles AtelierCapture. It needs no game files or private-server files. The install ZIP is written to `../capture-release`; build dependencies and outputs stay outside the source checkout.

To rebuild the capture alone after that:

```powershell
$captureBuild = (Resolve-Path ../capture-build).Path
dotnet build Sources/AtelierCapture/AtelierCapture.csproj -c Release `
  "-p:RuntimeDirectory=$captureBuild/stage/BepInEx/core" `
  "-p:ReferenceDirectory=$captureBuild/dependencies" -p:Version=1.3.0
```

For individual custom-runtime commands, see [Sources/README.md](Sources/README.md). Git contains source and build files; installers are published under Releases. Update `VERSION` and `CHANGELOG.md` for a release. Pull requests build the package; pushes to `main` publish it.
