# Source and builds

These folders contain the capture and modified runtime source. Build dependencies
are downloaded separately; no runtime or game-generated assemblies are stored here.

| Folder | Upstream base | Changes |
| --- | --- | --- |
| `AtelierCapture/` | This repository | API/combat capture, archive writer, manifest and recovery |
| `BepInEx/` | [BepInEx `5b766a3`](https://github.com/BepInEx/BepInEx/commit/5b766a3b7f6c164d4798924a93f3acf4db769d06) | HarmonyX/MonoMod/Il2CppInterop dependency updates and adaptations for the newer detour and console APIs |
| `Il2CppInterop/` | [Il2CppInterop `dbda1cb`](https://github.com/BepInEx/Il2CppInterop/commit/dbda1cb353b0f4253345dc45136d170b9e50a5a0) | HarmonyX 2.16.1 dependency and Windows x64 value-type/nullable argument handling in `DetourMethodPatcher` |

HarmonyX is the unmodified [2.16.1 release](https://github.com/BepInEx/HarmonyX/tree/v2.16.1).
Licenses are included here and in `../Licenses`.

## Build the modified libraries

Install Python 3.11+ and the .NET 8 SDK. The projects target .NET 6 to match the
supported runtime. Run from the repository root in PowerShell:

```powershell
py scripts/build.py --workdir ../capture-build --version 1.3.0 --source-commit (git rev-parse HEAD)
py scripts/package.py --stage ../capture-build/stage --output ../capture-release
```

Use a new work directory for each full build. The script verifies downloaded
dependencies and builds the custom runtime, HarmonySupport and capture. Outputs
and references stay outside the checkout. Packaging requires a matching changelog.

## Individual projects

After the full build has downloaded the public reference files, run these from
the repository root to rebuild each project separately:

```powershell
$captureBuild = (Resolve-Path ../capture-build).Path
$runtimeSource = (Resolve-Path Sources/BepInEx).Path

dotnet build Sources/BepInEx/Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj -c Release `
  "-p:SolutionDir=$runtimeSource/" "-p:BuildDir=$captureBuild/manual/BepInEx" `
  "-p:BuildArtifactsDirectory=$captureBuild/manual/BepInEx/" `
  "-p:UnityReferenceDirectory=$captureBuild/dependencies" `
  -p:GeneratePackageOnBuild=false -p:VersionSuffix=be.788-capture.1

dotnet build Sources/Il2CppInterop/Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj -c Release `
  "-p:BuildArtifactsDirectory=$captureBuild/manual/Il2CppInterop/" `
  "-p:Il2CppReferenceDirectory=$captureBuild/dependencies" `
  -p:GeneratePackageOnBuild=false -p:VersionSuffix=capture.2

dotnet build Sources/AtelierCapture/AtelierCapture.csproj -c Release `
  "-p:RuntimeDirectory=$captureBuild/stage/BepInEx/core" `
  "-p:ReferenceDirectory=$captureBuild/dependencies" -p:Version=1.3.0
```

Keep references out of `BepInEx/core`; they are compilation inputs. Use the full
build for an installer so matching runtime and plugin files are packaged together.

## Publish an update

Commit the source and build-file changes. Pull requests compile and package
without publishing. Main builds publish the installer, checksums, release
manifest and GitHub provenance. The capture needs no game or private-server files.

Every push to `main` runs the Release workflow. It creates an install ZIP and
SHA-256 checksum, then publishes a release. The first version comes from
`VERSION`; later releases increment the highest release tag's patch number.
Raise `VERSION` when a new major or minor version is needed and add that version's
entry to `CHANGELOG.md`. The capture embeds the package version. Rerunning an
already published commit keeps its release instead of creating another.

The install ZIP excludes `Sources`, scripts, GitHub files and build outputs.
Sources remain available in this repository and each release's source downloads.
Use the two build/package commands above for a local installer check.
