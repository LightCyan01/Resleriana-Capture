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

## Build the complete package

Follow the [Windows build guide](../README.md#build-from-source) for prerequisites,
source checkout, building and packaging. That build compiles the modified BepInEx
and HarmonySupport libraries along with AtelierCapture, then assembles the installer.

The projects target .NET 6 for the supported runtime; the repository uses the
.NET 8 SDK to compile them.

## Individual projects

Use these commands when working on one project. First finish the full build in
the guide above so the runtime and public reference files are available.

Open PowerShell in the `Resleriana-Capture` folder, where `scripts` and `Sources`
are located. Set these paths once; if you chose a different build folder, replace
`../capture-build` with that folder:

```powershell
$captureBuild = (Resolve-Path ../capture-build).Path
$runtimeSource = (Resolve-Path Sources/BepInEx).Path
$captureVersion = (Get-Content VERSION -Raw).Trim()
```

### BepInEx

```powershell
dotnet build Sources/BepInEx/Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj -c Release `
  "-p:SolutionDir=$runtimeSource/" "-p:BuildDir=$captureBuild/manual/BepInEx" `
  "-p:BuildArtifactsDirectory=$captureBuild/manual/BepInEx/" `
  "-p:UnityReferenceDirectory=$captureBuild/dependencies" `
  -p:GeneratePackageOnBuild=false -p:VersionSuffix=be.788-capture.1
```

The runtime DLLs are written to `../capture-build/manual/BepInEx/Unity.IL2CPP/`.

### HarmonySupport

```powershell
dotnet build Sources/Il2CppInterop/Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj -c Release `
  "-p:BuildArtifactsDirectory=$captureBuild/manual/Il2CppInterop/" `
  "-p:Il2CppReferenceDirectory=$captureBuild/dependencies" `
  -p:GeneratePackageOnBuild=false -p:VersionSuffix=capture.2
```

The DLL is written to `../capture-build/manual/Il2CppInterop/bin/Il2CppInterop.HarmonySupport/net6.0/Il2CppInterop.HarmonySupport.dll`.

### AtelierCapture

```powershell
dotnet build Sources/AtelierCapture/AtelierCapture.csproj -c Release `
  "-p:RuntimeDirectory=$captureBuild/stage/BepInEx/core" `
  "-p:ReferenceDirectory=$captureBuild/dependencies" `
  "-p:BaseIntermediateOutputPath=$captureBuild/manual/AtelierCapture/obj/" `
  "-p:OutputPath=$captureBuild/manual/AtelierCapture/" `
  -p:AppendTargetFrameworkToOutputPath=false "-p:Version=$captureVersion"
```

The DLL is written to `../capture-build/manual/AtelierCapture/AtelierCapture.dll`.

For a local installation, close the game before copying your rebuilt DLLs:

| Rebuilt files | Destination in the game folder |
| --- | --- |
| BepInEx runtime DLLs | `BepInEx/core/` |
| `Il2CppInterop.HarmonySupport.dll` | `BepInEx/core/` |
| `AtelierCapture.dll` | `BepInEx/plugins/` |

Keep files from `capture-build/dependencies` as build inputs; copy only the
rebuilt DLLs listed above. For a complete installer, run the full build with a
fresh work directory to assemble matching runtime and plugin files.

## Publish an update

Commit the source and build-file changes. Pull requests compile and package
without publishing. Main builds publish the installer, checksums, release
manifest and GitHub provenance. The capture needs no game or private-server files.

The Release workflow runs on pushes to `main`. It creates an install ZIP and
SHA-256 checksum, then publishes a release. The first version comes from
`VERSION`; later releases increment the highest release tag's patch number.
Raise `VERSION` when a new major or minor version is needed. Add a `CHANGELOG.md`
entry for every selected release version, including automatic patch releases.
The capture embeds the package version. Rerunning an
already published commit keeps its release instead of creating another.

For a documentation-only update, add `[skip ci]` to the commit message to skip
the workflow and keep the current installer release.

The install ZIP excludes `Sources`, scripts, GitHub files and build outputs.
Sources remain available in this repository and each release's source downloads.
Use the Windows build guide for a local installer check.
