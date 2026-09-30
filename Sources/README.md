# Modified runtime source

These folders contain the source used for the modified runtime libraries in
this package, including their upstream build reference assemblies. They exclude
local build outputs, caches and game-generated interop files.

| Folder | Upstream base | Changes |
| --- | --- | --- |
| `BepInEx/` | [BepInEx `5b766a3`](https://github.com/BepInEx/BepInEx/commit/5b766a3b7f6c164d4798924a93f3acf4db769d06) | HarmonyX/MonoMod/Il2CppInterop dependency updates and adaptations for the newer detour and console APIs |
| `Il2CppInterop/` | [Il2CppInterop `dbda1cb`](https://github.com/BepInEx/Il2CppInterop/commit/dbda1cb353b0f4253345dc45136d170b9e50a5a0) | HarmonyX 2.16.1 dependency and Windows x64 value-type/nullable argument handling in `DetourMethodPatcher` |

HarmonyX is the unmodified [2.16.1 release](https://github.com/BepInEx/HarmonyX/tree/v2.16.1).
Licenses are included here and in `../Licenses`.

## Build the modified libraries

Use a .NET SDK that supports the projects' .NET 6 targets. From `Sources/BepInEx`,
run in PowerShell:

```powershell
dotnet build Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj -c Release `
  "-p:SolutionDir=$PWD\" -p:GeneratePackageOnBuild=false `
  -p:VersionSuffix=be.788-capture.1 `
  -p:SourceRevisionId=5b766a3b7f6c164d4798924a93f3acf4db769d06
```

From `Sources/Il2CppInterop`:

```powershell
dotnet build Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj -c Release `
  -p:GeneratePackageOnBuild=false -p:VersionSuffix=capture.2 `
  -p:SourceRevisionId=dbda1cb353b0f4253345dc45136d170b9e50a5a0
```

With the game closed, copy BepInEx's `bin/Unity.IL2CPP` runtime DLLs into
`BepInEx/core`. Then replace `Il2CppInterop.HarmonySupport.dll` with the one from
`bin/Il2CppInterop.HarmonySupport/net6.0`. Keep the other packaged Il2CppInterop
1.5.3 libraries. Build reference assemblies do not belong in `BepInEx/core`.

## Publish an update

Rebuild and check changed plugins or runtime libraries locally, then commit the
updated DLLs along with their source changes. Capture compilation needs the
supported game's generated interop assemblies, which are not shipped here.
The workflow packages the committed binaries; it does not compile the game or
the runtime sources.

Every push to `main` runs the Release workflow. It creates an install ZIP and
SHA-256 checksum, then publishes a release. The first version comes from
`VERSION`; later releases increment the highest release tag's patch number.
Raise `VERSION` when a new major or minor version is needed. Package versions
are separate from the versions embedded in individual DLLs. Rerunning an
already published commit keeps its release instead of creating another.

The install ZIP excludes `Sources`, scripts, GitHub files and build outputs.
Sources remain available in this repository and each release's source downloads.
For a local package check, run `py scripts/package.py --output dist` from the
repository root.
