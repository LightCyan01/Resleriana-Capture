# Account and session data recorded

By default, Resleriana Capture preserves the API bodies, decoded fields and headers it observes in the JP Steam client. Account identifiers, login credentials and session tokens are saved with their original values. The recorder also saves native gameplay observations and diagnostic logs. This describes 1.4.0 and the default configuration of 1.4.1 and later.

Captures stay on your PC until you share them. Anyone receiving a capture can read its contents. Base64 encoding, ZIP compression and integrity hashes do not hide the recorded values.

## Optional privacy mode in 1.4.1

Set `enabled` to `true` in `BepInEx/config/AtelierCapture.Privacy.json` while the game is closed, then restart it. The default is `false`.

Enabled mode retains qualified numeric gameplay progress, quantities, catalog and owned-entity references, ordered inputs/results and typed timestamps. It excludes account and device identifiers, credentials, profile details and arbitrary text. Original body bytes, unknown fields, headers, URLs, free-form diagnostics and startup log copies are omitted.

In 1.4.1, privacy mode also omitted every native snapshot value. From 1.4.2, qualified Battle and Atelier gameplay arguments, results and state remain, including a filtered decoded view of native protobuf messages. Public probability values, asset and store-product references, and numeric mail-template parameters also remain where their exact field contract is qualified. Unsupported fields have explicit omissions. The manifest records the policy version; a policy-1 recording cannot recover values it omitted.

The retained progress can still be recognizable. This setting does not guarantee anonymity, erase existing captures or alter the game's external BepInEx/Unity log files. Recovery in privacy mode leaves older full or unknown-policy recordings untouched. Full original capture remains useful for research and a possible future account-progress import; filtered captures have explicit omissions and cannot promise a complete import.

## Login and session fields

The following fields have been observed in login and authenticated traffic:

| Where                         | Recorded fields                                 | Meaning                                                                     |
| ----------------------------- | ----------------------------------------------- | --------------------------------------------------------------------------- |
| `/auth/sign_in` request       | `deviceSecret`, `deviceUniqueId`, `deviceModel` | The game's device credential, device identifier and device model.           |
| `/auth/sign_in` response      | `sessionToken`, `userId`, `language`            | The returned authentication token, account identifier and language setting. |
| Authenticated request headers | `x-session-token`, `x-user-id`                  | The token and account identifier sent with subsequent game requests.        |
| `/status` response            | `device_auth`                                   | The authentication-related value returned by the status endpoint.           |

All observed request and response headers are retained, including their values. This also records `x-request-id`, platform, client, asset and master-data versions. Authorization or cookie headers would be retained if present; their presence is not required for a capture.

Which additional credentials appear depends on the game flows used during recording. Supported API contracts include:

| Flow                                   | Fields retained when present                                       |
| -------------------------------------- | ------------------------------------------------------------------ |
| Device sign-in                         | Optional `deviceToken` and the returned `deviceChanged` flag.      |
| Account transfer using a passcode      | `userId`, `passcode` and the device fields sent in the request.    |
| Apple or Google account linking        | `idToken`; Google linking can also send `code` and `codeVerifier`. |
| Web-session or KTID web-token requests | The returned `token`.                                              |
| Advertising identifier updates         | `idfa`, `gpsAdid`, `adid`.                                         |

These flows do not occur in every session. The recorder preserves their data when observed; it does not initiate them to collect extra credentials. Unknown fields also remain in the original body bytes, even when the decoded view cannot name them. The tables therefore describe known fields rather than an exhaustive filter on sensitive data.

Credential validity and expiry depend on the official service. Closing the game does not establish that all recorded credentials have expired.

## Profile and account progress

When recording includes `/user/log_in`, its response supplies an account snapshot. The recorder retains the returned resources and later observed changes, including:

- Profile name and memo, favorite characters and party, selected home and account creation timestamp.
- Account status fields such as `birthYear`, `birthMonth`, rank, EXP, stamina, mana and tutorial progress.
- Currency balances, including separate paid and free balances, and inventory quantities.
- Owned characters, growth and skill state, character pieces, equipment, traits, memoria, skins and emblems.
- Parties, members, equipment presets and rental formations.
- Quest and story progress, missions, reward-claim states and illustrated-book progress.
- Recipes, research, expeditions, shop purchase counters and other returned resource sections.

Subsequent requests and responses record actions and their results, such as selected skills, synthesis materials, rewards, resource changes, gacha results and mail contents when those calls occur. Returned purchase or receipt information is also retained when present in observed traffic.

The snapshot covers what the game returns at login. Other data may be fetched only when visiting a screen or performing an action. Starting recording after login does not reconstruct the missed response, and the capture cannot supply backend state that was never observed.

## Where the data appears in a capture

| File                | Account and session information                                                                                                                                                                                                        |
| ------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `api.jsonl`         | Decoded login and account responses, request inputs, observed headers, routes, URLs, request identifiers and timestamps.                                                                                                               |
| `raw-bodies.jsonl`  | Original observed body bytes in Base64, alongside byte lengths and SHA-256 hashes. Credentials and unknown fields remain in those bytes. Derived and native-reserialized representations have separate stage labels.                   |
| `combat.jsonl`      | Battle and Atelier inputs, results and declared gameplay state, including owned entity identifiers, formations and synthesis selections.                                                                                               |
| `diagnostics.jsonl` | Header observations, native parsing inputs, request correlation, session timing, errors and stack traces. Startup snapshots preserve available prefixes of BepInEx and Unity logs, including any sensitive text already written there. |
| `manifest.json`     | Recorder, client and runtime identity, run ID, timestamps, readable counts, integrity facts and recording gaps. The login credentials are not intentionally copied into the manifest.                                                  |

Diagnostic paths and exception messages can reveal local installation paths or a Windows user-folder name. The recorder's `run_id` identifies a recording; it is separate from the game's authentication `sessionToken`. Headers, parsing observations and logs can duplicate information from API records, so removing a field from one decoded record does not remove every copy from the archive.

The recorder saves complete supported observations and reports failed reads, missing hooks and uncertain ownership as gaps. A valid archive or normal shutdown does not establish that every account field or gameplay mechanic was observed. Queued warnings and known material errors can be lost during abrupt termination; API and gameplay observations wait for durable saving.

## Source references

- [Header capture](../Sources/AtelierCapture/Network/ApiHeaders.cs) and [request observations](../Sources/AtelierCapture/Network/NativeRequestObservers.cs).
- [Raw body preservation](../Sources/AtelierCapture/Network/ApiBodies.cs) and [API route contracts](../Sources/AtelierCapture/Routes.json).
- [Startup and diagnostic setup](../Sources/AtelierCapture/Plugin.cs) and [original log preservation](../Sources/AtelierCapture/Diagnostics/OriginalLogSnapshot.cs).
- [Archive format and evidence limits](../Sources/AtelierCapture/FORMAT.md).
