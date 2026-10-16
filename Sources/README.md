# Source and builds

These folders contain the capture and modified runtime source. Build dependencies
are downloaded separately; no runtime or game-generated assemblies are stored here.

| Folder                   | Upstream base                                                                                                       | Changes                                                                                                                                   |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `AtelierCapture/`        | This repository                                                                                                     | API/combat capture, archive writer, manifest and recovery                                                                                 |
| `AtelierCapture.Native/` | This repository                                                                                                     | Finite MSVC Windows x64 wrappers preserve original native receiver, argument, return and exception behavior while callbacks observe data. |
| `BepInEx/`               | [BepInEx `5b766a3`](https://github.com/BepInEx/BepInEx/commit/5b766a3b7f6c164d4798924a93f3acf4db769d06)             | HarmonyX/MonoMod/Il2CppInterop dependency updates and adaptations for the newer detour and console APIs                                   |
| `Il2CppInterop/`         | [Il2CppInterop `dbda1cb`](https://github.com/BepInEx/Il2CppInterop/commit/dbda1cb353b0f4253345dc45136d170b9e50a5a0) | Current scanner backport, HarmonyX 2.16.1, Windows x64 value-type/nullable handling and owned detour lifetime fixes                       |

HarmonyX is the unmodified [2.16.1 release](https://github.com/BepInEx/HarmonyX/tree/v2.16.1).
Licenses are included here and in `../Licenses`.

The build applies PF17 to the pinned localization companion 2.0.0 DLL: one
four-byte branch operand redirects untouched CABs to its existing copy block.
Only one byte changes. Input/output SHA256s and the exact method/PE offset are
recorded in dependency and release manifests; assembly version stays 2.0.0.
English localization remains enabled. Its process-local cache requires a fresh
process and stable source bytes/locale; original game bundles are preserved.

## Build the complete package

Install the tools listed in the [Windows build guide](../README.md#build-from-source),
then run `py scripts/build.py` from the repository root. It compiles BepInEx,
HarmonySupport, AtelierCapture.Native and AtelierCapture, and puts the install ZIP in `work/release`.

The recorder targets .NET 10 / C# 14. Vendored components keep compatible target
frameworks and run in the coherent embedded .NET 10.0.13 host. The build verifies
the portable .NET 10.0.402 SDK and public dependencies inside this workspace.

## Individual projects

Use these commands when working on one project. First finish the full build in
the guide above so the runtime and public reference files are available.

Open PowerShell in the repository root and set these paths:

```powershell
$captureBuild = (Resolve-Path work/build).Path
$captureDotnet = (Resolve-Path work/toolchain/dotnet10/dotnet.exe).Path
$runtimeSource = (Resolve-Path Sources/BepInEx).Path
$captureVersion = (Get-Content VERSION -Raw).Trim()
```

### BepInEx

```powershell
& $captureDotnet build Sources/BepInEx/Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj -c Release `
  "-p:SolutionDir=$runtimeSource/" "-p:BuildDir=$captureBuild/manual/BepInEx" `
  "-p:BuildArtifactsDirectory=$captureBuild/manual/BepInEx/" `
  "-p:UnityReferenceDirectory=$captureBuild/dependencies" `
  -p:GeneratePackageOnBuild=false -p:Version=6.0.0-be.788-capture.2
```

The runtime DLLs are written to `work/build/manual/BepInEx/Unity.IL2CPP/`.

### HarmonySupport

```powershell
& $captureDotnet build Sources/Il2CppInterop/Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj -c Release `
  "-p:BuildArtifactsDirectory=$captureBuild/manual/Il2CppInterop/" `
  "-p:Il2CppReferenceDirectory=$captureBuild/dependencies" `
  -p:GeneratePackageOnBuild=false -p:Version=1.5.3-capture.3
```

The DLL is written to `work/build/manual/Il2CppInterop/bin/Il2CppInterop.HarmonySupport/net6.0/Il2CppInterop.HarmonySupport.dll`.

### AtelierCapture

```powershell
& $captureDotnet build Sources/AtelierCapture/AtelierCapture.csproj -c Release `
  "-p:RuntimeDirectory=$captureBuild/stage/BepInEx/core" `
  "-p:ReferenceDirectory=$captureBuild/dependencies" `
  "-p:BaseIntermediateOutputPath=$captureBuild/manual/AtelierCapture/obj/" `
  "-p:OutputPath=$captureBuild/manual/AtelierCapture/" `
  -p:AppendTargetFrameworkToOutputPath=false "-p:Version=$captureVersion"
```

The DLL is written to `work/build/manual/AtelierCapture/AtelierCapture.dll`.

For a local installation, close the game before copying your rebuilt DLLs:

| Rebuilt files                      | Destination in the game folder |
| ---------------------------------- | ------------------------------ |
| BepInEx runtime DLLs               | `BepInEx/core/`                |
| `Il2CppInterop.HarmonySupport.dll` | `BepInEx/core/`                |
| `AtelierCapture.dll`               | `BepInEx/plugins/`             |

Keep `work/build/dependencies` as build inputs. Rebuild the complete package and
replace its coherent runtime graph; preserve original captures and configuration.
The runtime certificate binds all immutable installed dependencies.

## Publish an update

Build both full installers for the same version:

```powershell
py scripts/build.py
py scripts/build.py --manual-only
```

The automatic build goes to `work/release/`; the manual build goes to
`work/release-manual/` and omits all updater components. Their compiled mode and
runtime certificate must agree. Separate default work directories prevent one
build from replacing the other's staged files.

An ordinary build produces the full installer without an automatic-update ZIP.
The release maintainer creates that ZIP locally with the private signing key:

```powershell
py scripts/package.py --stage work/build/stage --output work/release --version X.Y.Z --signing-key "C:\path-outside-the-repository\release-private.pem"
```

Use the same version as the built stage. The helper checks that the key matches
its embedded public key and signs the exact update manifest with ECDSA P-256 and
SHA-256. Keep the private key outside the repository, release assets and GitHub
Actions. Source builds and verification do not need it. A new trusted key or a
change to any pinned base component requires a full manual installation.

When publishing the stable `vX.Y.Z` release, upload
`Resleriana-Capture-vX.Y.Z-win-x64-AutoUpdater.zip` and
`Resleriana-Capture-vX.Y.Z-update-win-x64.zip`, plus `release-manifest.json` and
`SHA256SUMS.txt` from the signed automatic package output. Also upload
`Resleriana-Capture-vX.Y.Z-win-x64.zip`, `release-manifest-manual.json` and
`SHA256SUMS-manual.txt` from the manual output to that same release. Use
`release-notes.md` for its description. Without the update ZIP, existing
automatic installations receive no automatic update. Manual packages never
produce an automatic-update ZIP. Do not upload the private key or temporary
signing inputs.

Routine builds and checks run locally. The Windows verification workflow has one
job and runs only through `workflow_dispatch`; it uploads a local package artifact
and never publishes a release. Pushes and pull requests trigger no workflow.
Publishing requires a separate explicit instruction after native/gameplay and
hardware qualification. Package manifests bind actual source bytes and binaries
separately from the Git commit; the commit field alone does not establish a clean checkout.

The install ZIP excludes `Sources`, scripts, GitHub files and build outputs.
Sources remain available in this repository and each release's source downloads.
Use the Windows build guide for a local installer check.
