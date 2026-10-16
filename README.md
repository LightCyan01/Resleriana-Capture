# Resleriana Capture

Resleriana Capture records API traffic from Atelier Resleriana (JP) on Steam, and saves each play session as a ZIP on your PC. The package also includes the English translation from [Localization 2.0.0](https://github.com/ongandrew/AtelierResleriana/releases/tag/2.0.0).

## Requirements

The Steam JP client, version 2.16.0.

## Install

1. Close the game. Download `Resleriana-Capture-vX.Y.Z-win-x64.zip` from the Assets of the [latest release](https://github.com/LightCyan01/Resleriana-Capture/releases/latest).
2. Open the game folder, the one that contains `AtelierResleriana.exe`. If it already has a `dotnet` or `BepInEx/core` folder, move it somewhere outside the game folder. The plugin checks the hashes of these files, so old and new copies can't be mixed.
3. Extract the ZIP into the game folder. Keep your existing captures, custom localization files and other plugins. If you already have files in `BepInEx/config`, keep yours and update them as described in [Upgrading an existing setup](#upgrading-an-existing-setup). Make sure the game folder has only one `AtelierCapture.dll`.
4. Launch the game through Steam. Recording starts on its own. The first launch can take a few minutes to set up.

### Upgrading an existing setup

If you kept your own `BepInEx/config/BepInEx.cfg`, check that its `[IL2CPP]` section has these values:

```ini
[IL2CPP]
UnityBaseLibrariesSource = 2022.3.62.zip
GlobalMetadataPath = {GameDataPath}/il2cpp_data/Metadata/global-metadata.dat
IL2CPPInteropAssembliesPath = {BepInEx}
```

The package includes the Unity 2022.3.62 base libraries, so BepInEx doesn't download them.

In `BepInEx/config/AtelierResleriana.Plugin.Localization.cfg`, set `AutoUpdate = false` under `[Data]`. This keeps the translation data at the version the package was checked with. Leave your language and other options as they are.

## Captures

After you close the game, your capture is here:

```text
BepInEx/plugins/Captures/Official/capture-YYYY-MM-DD_HH-mm-ss.zip
```

Each ZIP covers one run of the game in one environment, such as `Official`. If a file with that name already exists, the new one gets a suffix such as `-2`.

> [!IMPORTANT]
> Let the game close on its own. It builds and checks the ZIP while closing, and after a long session that can take a few minutes. If the game crashes or is killed, start it again and the next launch packs the saved recording. Leave any leftover working folders in place; the plugin keeps them for recovery.

Each ZIP holds four JSON Lines files and a manifest:

| File                | What it contains                                                                                                                                                                                                                   |
| ------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `api.jsonl`         | Decoded requests and responses with routes, response codes and every observed header. Depending on what you play, this covers characters, parties, inventory, currencies, quest progress, gacha results, mail and rewards.         |
| `combat.jsonl`      | Battle and Atelier method arguments, results and the game state around each call. Game state is read again at every call, and failed reads are recorded as gaps. UI and rendering internals are left out.                          |
| `raw-bodies.jsonl`  | The original request and response bodies in Base64, with byte lengths and SHA-256 hashes. Fields the decoded logs miss are still here.                                                                                             |
| `diagnostics.jsonl` | Session IDs, timestamps, request completion states, timings, errors and capture gaps. A warning that keeps repeating, or one of two known Unity material errors, is stored in full once and then counted.                          |
| `manifest.json`     | Sizes, record counts and SHA-256 hashes for each file, counts per system and per route, the game client and runtime versions, and any recording gaps. Counts are readable, not encrypted, and the manifest doesn't score captures. |

A capture covers only what you played. Hooks that couldn't be installed, failed reads and unclear object origins show up as gaps in the manifest and diagnostics.

> [!WARNING]
> Captures contain your account and session data. Bodies, decoded fields and headers are saved as captured, with no redaction or hashing. The plugin keeps recordings on your PC and doesn't upload them, but anyone you send a capture to can read that data.

## Long sessions

A capture has no size limit, and the recorder doesn't trim, sample or drop records to keep it small. Files over 4 GB are stored as ZIP64.

- The recorder waits for the disk rather than dropping data, so slow storage can make the game wait.
- If the drive fills up or fails, the game stops at the next recorded action and waits until there is space again or you abort the capture.
- Closing takes longer as the capture grows, because the plugin compresses everything and reads it back to check it.
- Closing needs free space for the ZIP while the working files still exist. The working files are deleted only after the ZIP checks out.

### Checking or stopping a capture

While the game is running, open PowerShell in the game folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\capture-control.ps1 -ProcessId (Get-Process AtelierResleriana).Id -Command status
```

The reply includes the current run ID. To abort the capture, pass that ID and a reason:

```powershell
powershell -ExecutionPolicy Bypass -File .\capture-control.ps1 -ProcessId (Get-Process AtelierResleriana).Id -Command abort -RunId <run ID> -Reason "<reason>"
```

Aborting marks the run incomplete and keeps the data already saved. It can't save data to a broken drive.

## Automatic capture updates

The updater checks for newer stable releases in the background, at most once per hour. Compatible updates download while you play and install after the game process exits. The current session keeps its original recorder version. A crash or forced close also releases the updater to install; if the updater is interrupted, the next launch finishes recovery before capture starts.

Automatic updates replace only the capture plugin, its native companion and their compatibility certificate. A release that changes BepInEx, the updater or the embedded runtime requires the full installer. Existing captures and recording recovery folders are preserved. Downloads that fail validation leave the installed recorder in place.

To disable automatic updates, set `Enabled = false` under `[Updates]` in `BepInEx/config/AtelierCapture.Updater.cfg`. Recovery of an already interrupted installation still runs. Local builds and releases with the same version number do not update automatically.

## How recording works

The plugin hooks the game's HTTP send, response decoding and completion paths. It keeps each original API body before decoding, so fields the decoder doesn't know about are kept. Battle and Atelier hooks copy the state they need while it is valid and hand the records to a background writer. The plugin doesn't poll every frame.

Each gameplay and network record is written to disk, along with a recovery checkpoint, before the game continues. Records that arrive while the disk is busy are saved together in one write. Warnings and known material errors don't wait for the disk, so a crash can lose the last few of them.

When the game closes, the writer builds the ZIP, reads it back to check every file against the saved data, and only then deletes the working copies. After a crash, recovery packs everything up to the last checkpoint and leaves anything after it in the working folder. Older folders that were already packed are kept as they are.

The recorder runs on a modified BepInEx 6.0.0-be.788 with HarmonyX 2.16.1. Native hooks go through a small Windows x64 forwarding layer that calls the original game method on its original object and keeps the game's own exception handling.

The manifest checks that the files are intact. It can't prove that a capture came from real gameplay, because a modified plugin could fabricate data and recompute the hashes. Release checksums identify the installer, not the recordings. The [archive format](https://github.com/LightCyan01/Resleriana-Capture/blob/main/Sources/AtelierCapture/FORMAT.md) describes the files, the validation rules and their limits.

## Build from source

Install these tools:

- [Git for Windows](https://git-scm.com/install/windows)
- [Python 3.11 or newer for Windows](https://www.python.org/downloads/windows/)
- Visual Studio 2022 C++ build tools with the Windows SDK, for the native forwarding layer

You don't need to install .NET. The build script downloads the version it needs.

Open Command Prompt or PowerShell in the folder where you want the source, then run:

```powershell
git clone https://github.com/LightCyan01/Resleriana-Capture.git
cd Resleriana-Capture
py scripts/build.py
```

This builds the modified BepInEx runtime, HarmonySupport, the native forwarding layer and the capture plugin, then writes `work/release/Resleriana-Capture-vX.Y.Z-win-x64.zip`. The first build needs an internet connection to download checksum-pinned public dependencies. Later builds can use `--offline`. You don't need the game installed to build.

Install the result as described under [Install](#install). After editing the source, run `py scripts/build.py` again.

## More documentation

- [Archive format](https://github.com/LightCyan01/Resleriana-Capture/blob/main/Sources/AtelierCapture/FORMAT.md): every file and manifest field, and how captures are validated.
- [Source and builds](https://github.com/LightCyan01/Resleriana-Capture/blob/main/Sources/README.md): what each source folder changes, single-project builds and releases.
- [Changelog](https://github.com/LightCyan01/Resleriana-Capture/blob/main/CHANGELOG.md)
- [Third-party licenses](https://github.com/LightCyan01/Resleriana-Capture/blob/main/Licenses/README.md)
