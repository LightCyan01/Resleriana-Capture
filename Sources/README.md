# Modified runtime source

These archives contain the source used for the modified runtime libraries in
this package, including their upstream build reference assemblies. They exclude
local build outputs, caches and game-generated interop files.

| Archive | Upstream base | Changes |
| --- | --- | --- |
| `BepInEx-788-capture.1-source.zip` | [BepInEx `5b766a3`](https://github.com/BepInEx/BepInEx/commit/5b766a3b7f6c164d4798924a93f3acf4db769d06) | HarmonyX/MonoMod/Il2CppInterop dependency updates and adaptations for the newer detour and console APIs |
| `Il2CppInterop-1.5.3-capture.2-source.zip` | [Il2CppInterop `dbda1cb`](https://github.com/BepInEx/Il2CppInterop/commit/dbda1cb353b0f4253345dc45136d170b9e50a5a0) | HarmonyX 2.16.1 dependency and Windows x64 value-type/nullable argument handling in `DetourMethodPatcher` |

HarmonyX is the unmodified [2.16.1 release](https://github.com/BepInEx/HarmonyX/tree/v2.16.1).
Licenses are included here and in `../Licenses`.

## Build the modified libraries

Use a .NET SDK that supports the projects' .NET 6 targets. Extract each archive
separately. From the extracted BepInEx source folder, run in PowerShell:

```powershell
dotnet build Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj -c Release `
  "-p:SolutionDir=$PWD\" -p:GeneratePackageOnBuild=false `
  -p:VersionSuffix=be.788-capture.1 `
  -p:SourceRevisionId=5b766a3b7f6c164d4798924a93f3acf4db769d06
```

From the extracted Il2CppInterop source folder:

```powershell
dotnet build Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj -c Release `
  -p:GeneratePackageOnBuild=false -p:VersionSuffix=capture.2 `
  -p:SourceRevisionId=dbda1cb353b0f4253345dc45136d170b9e50a5a0
```

With the game closed, copy BepInEx's `bin/Unity.IL2CPP` runtime DLLs into
`BepInEx/core`. Then replace `Il2CppInterop.HarmonySupport.dll` with the one from
`bin/Il2CppInterop.HarmonySupport/net6.0`. Keep the other packaged Il2CppInterop
1.5.3 libraries. Build reference assemblies do not belong in `BepInEx/core`.
