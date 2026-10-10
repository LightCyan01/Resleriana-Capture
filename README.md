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

The manifest checks file integrity. A modified plugin can fabricate data and recompute its hashes, so the manifest does not prove official gameplay. Release checksums and GitHub build attestations identify the published installer; they do not authenticate recordings. See [the archive format](https://github.com/LightCyan01/Resleriana-Capture/blob/main/Sources/AtelierCapture/FORMAT.md) for the validation contract and limits.

## Build from source

These steps build the published v1.3.0 release on Windows. The build includes the modified BepInEx runtime, HarmonySupport, AtelierCapture and English translation. It downloads the required public dependencies and checks their hashes. You can build it without installing the game.

### 1. Install the build tools

- [Git for Windows](https://git-scm.com/install/windows).
- [Python for Windows](https://www.python.org/downloads/windows/), version 3.11 or later.
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), the Windows x64 installer under **SDK**.

After installing them, open PowerShell and check:

```powershell
git --version
py --version
dotnet --list-sdks
```

Git and Python should print their versions. The SDK list must include `8.0.x`. The repository selects .NET 8 through `global.json`, even if a newer SDK is also installed.

### 2. Get the source

Create a folder for your build, outside the game folder. Open it in File Explorer, click the address bar, type `powershell` and press Enter. Run:

```powershell
git clone --branch v1.3.0 --depth 1 https://github.com/LightCyan01/Resleriana-Capture.git
cd Resleriana-Capture
```

This checks out the release source. Git may call it a detached HEAD; that is expected when checking out a release tag. Keep this PowerShell window open for the remaining steps.

### 3. Build the runtime and capture plugin

Run this command from the `Resleriana-Capture` folder:

```powershell
py scripts/build.py --workdir ../capture-build --version 1.3.0 --source-commit (git rev-parse HEAD)
```

The first build downloads dependencies and restores NuGet packages, so it needs an internet connection. Wait until it prints `Install files built at ...` before continuing. If it reports an error, use the troubleshooting section below.

The script creates `capture-build` beside the source folder. It keeps downloaded dependencies, compiled libraries and the assembled install files there.

### 4. Create the install ZIP

In the same PowerShell window, run:

```powershell
py scripts/package.py --stage ../capture-build/stage --output ../capture-release
```

When it succeeds, it prints the version, file count and ZIP size. Open the `capture-release` folder beside `Resleriana-Capture` to find:

```text
Resleriana-Capture-v1.3.0-win-x64.zip
```

Install that ZIP using the [Install](#install) steps above. The three folders have different purposes:

| Folder | Contents |
| --- | --- |
| `Resleriana-Capture` | Source code and build scripts. |
| `capture-build` | Downloaded dependencies, compiled files and the install staging folder. |
| `capture-release` | The finished installer ZIP, checksums and release metadata. |

### Troubleshooting

| Problem | What to do |
| --- | --- |
| `git`, `py` or `dotnet` is not recognized | Install the matching tool from step 1, then close and reopen PowerShell. Return to the `Resleriana-Capture` folder. |
| .NET cannot find a compatible SDK | Check `dotnet --list-sdks` for an `8.0.x` entry. Install the .NET 8 SDK if it is missing. |
| `FileExistsError` names `capture-build/stage` | Use a fresh build folder, such as `../capture-build-2`, in step 3. Use `../capture-build-2/stage` in step 4 too. |
| Packaging reports missing changelog notes | Check that you followed step 2 and used version `1.3.0` in step 3. The release tag keeps the plugin version and package notes together. |

For another full build, choose a fresh build folder as described above. To rebuild only AtelierCapture, BepInEx or HarmonySupport, use the [individual-project instructions](Sources/README.md#individual-projects).

Installers are published under Releases; this repository contains the source and build files. Contributors can find the [release steps](Sources/README.md#publish-an-update) in the source guide.
