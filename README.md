# Resleriana Capture

Record API traffic and combat data from Atelier Resleriana (JP) on Steam. Includes English translation from [Localization 2.0.0](https://github.com/ongandrew/AtelierResleriana/releases/tag/2.0.0). For Windows x64, game version 2.16.0.

Built on a modified BepInEx 6.0.0-be.788 with HarmonyX 2.16.1. The custom hook fixes are in Il2CppInterop's HarmonySupport, where they handle the game's Windows x64 value-type and nullable arguments.

The plugin records requests and responses inside the game, plus inputs, results and state before and after hooked combat methods. Request IDs connect battle events to API traffic. Keeping both decoded data and original bodies lets researchers inspect gameplay now and decode more fields later. Log writing and compression run on a background thread.

## Install

1. Close the game. Select **Code > Download ZIP** on this page and extract the download.
2. Copy the extracted contents into the game folder, beside `AtelierResleriana.exe`.
3. Launch the game through Steam. Recording starts automatically. The first launch may take a few minutes to set up.

## Captures

After closing the game, find your capture here:

```text
BepInEx/plugins/Captures/Official/capture-YYYY-MM-DD_HH-mm-ss.zip
```

Each ZIP contains four JSON Lines text files. Extract it once to read them.

| File | Information recorded |
| --- | --- |
| `api.jsonl` | Decoded requests and responses, routes, response codes and selected headers. Depending on what you play, this includes characters, parties, inventory, currencies, quest progress, gacha results, mail and rewards. |
| `combat.jsonl` | Hooked battle methods and their inputs/results, unit stats, HP, damage, break, buffs/debuffs, gauges, targets and turn order. |
| `raw-bodies.jsonl` | Original API request and response bodies stored as Base64, with byte lengths and SHA-256 hashes. These preserve data that the decoded logs may omit. |
| `diagnostics.jsonl` | Session identifiers, timestamps, request completion states, timings, errors and capture gaps. |

Let the game finish closing so it can save the ZIP. If it crashes, launch it again to recover the saved part of the recording. Leave any `.partial` folders in place until then. A capture covers the actions you played; limits or failures can leave gaps.
