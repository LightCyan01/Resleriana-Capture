"""Build the modified runtime and capture plugin from public sources."""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile

from package import ROOT, git, install_file, parse_version, sha256

BEPINEX_COMMIT = "5b766a3b7f6c164d4798924a93f3acf4db769d06"
INTEROP_COMMIT = "dbda1cb353b0f4253345dc45136d170b9e50a5a0"
DEPENDENCIES = {
    "bepinex.zip": (
        "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip",
        "f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a"),
    "localization.zip": (
        "https://github.com/ongandrew/AtelierResleriana/releases/download/2.0.0/AtelierResleriana.Plugin.Localization-2.0.0.zip",
        "3b56e6ecaa413fbc309442adc9f55c93005efe362238ee61d3d8db0349130da0"),
    "Unity.InputSystem.dll": (
        f"https://raw.githubusercontent.com/BepInEx/BepInEx/{BEPINEX_COMMIT}/Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/Unity.InputSystem.dll",
        "daffaa004643904cdf61b6a399b703edb7c8b9c599257fa835c82bb7cee47e79"),
    "UnityEngine.CoreModule.dll": (
        f"https://raw.githubusercontent.com/BepInEx/BepInEx/{BEPINEX_COMMIT}/Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/UnityEngine.CoreModule.dll",
        "4bcc37f551deec1d85256cb5941241724680fbe98a94b19b29ec6f92dc7720d6"),
    "UnityEngine.InputLegacyModule.dll": (
        f"https://raw.githubusercontent.com/BepInEx/BepInEx/{BEPINEX_COMMIT}/Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/UnityEngine.InputLegacyModule.dll",
        "a888a2929261dd2cf764264f5bd697aa9251525bf4a5491743addea5e989066c"),
    "Il2Cppmscorlib.dll": (
        f"https://raw.githubusercontent.com/BepInEx/Il2CppInterop/{INTEROP_COMMIT}/Il2CppInterop.Runtime/Libs/Il2Cppmscorlib.dll",
        "ee6176648f56660e32dc354a9063197ee2c2022f8ec8df3b177516d0fe671948"),
}


def download(name, directory):
    url, expected = DEPENDENCIES[name]
    path = directory / name
    if path.exists() and sha256(path) == expected:
        return path
    temporary = path.with_suffix(path.suffix + ".partial")
    request = urllib.request.Request(url, headers={"User-Agent": "Resleriana-Capture-build"})
    with urllib.request.urlopen(request, timeout=60) as response, temporary.open("wb") as output:
        shutil.copyfileobj(response, output)
    if sha256(temporary) != expected:
        raise ValueError(f"Dependency checksum mismatch: {name}")
    temporary.replace(path)
    return path


def extract(archive_path, stage, plugins_only=False):
    with zipfile.ZipFile(archive_path) as archive:
        for entry in archive.infolist():
            if entry.is_dir() or not install_file(entry.filename):
                continue
            if plugins_only and not entry.filename.startswith("BepInEx/plugins/"):
                continue
            destination = (stage / entry.filename).resolve()
            if not destination.is_relative_to(stage.resolve()):
                raise ValueError(f"Unsafe dependency archive path: {entry.filename}")
            destination.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, destination.open("wb") as output:
                shutil.copyfileobj(source, output)


def build(project, **properties):
    subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--nologo",
                    "-p:GeneratePackageOnBuild=false", "-p:ContinuousIntegrationBuild=true",
                    f"-p:PathMap={ROOT}=/_/Resleriana-Capture",
                    *[f"-p:{name}={value}" for name, value in properties.items()]], cwd=project.parent, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workdir", type=Path, default=ROOT / ".work")
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    parser.add_argument("--version")
    parser.add_argument("--source-commit")
    args = parser.parse_args()
    args.version = ".".join(map(str, parse_version(args.version or (ROOT / "VERSION").read_text().strip())))
    args.source_commit = args.source_commit or git("rev-parse", "HEAD")
    if not re.fullmatch(r"[a-f0-9]{40}", args.source_commit):
        parser.error("--source-commit must be a full Git commit hash")
    workdir = args.workdir.resolve()
    if workdir.is_relative_to(ROOT) and workdir != ROOT / ".work":
        parser.error("--workdir must be .work or outside the source checkout")
    stage = workdir / "stage"
    stage.mkdir(parents=True, exist_ok=True)
    dependencies = workdir / "dependencies"
    dependencies.mkdir(exist_ok=True)
    for name in DEPENDENCIES:
        download(name, dependencies)
    extract(dependencies / "bepinex.zip", stage)
    extract(dependencies / "localization.zip", stage, plugins_only=True)

    bepinex = ROOT / "Sources/BepInEx"
    interop = ROOT / "Sources/Il2CppInterop"
    compiled = workdir / "compiled"
    build(bepinex / "Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj",
          SolutionDir=str(bepinex) + "/", BuildDir=str(compiled / "BepInEx"),
          BuildArtifactsDirectory=str(compiled / "BepInEx") + "/",
          UnityReferenceDirectory=dependencies, Version="6.0.0-be.788-capture.1", SourceRevisionId=BEPINEX_COMMIT)
    runtime = stage / "BepInEx/core"
    for path in (compiled / "BepInEx/Unity.IL2CPP").glob("*.dll"):
        shutil.copyfile(path, runtime / path.name)

    build(interop / "Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj",
          BuildArtifactsDirectory=str(compiled / "Il2CppInterop") + "/",
          Il2CppReferenceDirectory=dependencies, Version="1.5.3-capture.2", SourceRevisionId=INTEROP_COMMIT)
    shutil.copyfile(compiled / "Il2CppInterop/bin/Il2CppInterop.HarmonySupport/net6.0/Il2CppInterop.HarmonySupport.dll",
                    runtime / "Il2CppInterop.HarmonySupport.dll")

    capture_output = compiled / "AtelierCapture"
    build(ROOT / "Sources/AtelierCapture/AtelierCapture.csproj", RuntimeDirectory=runtime,
          ReferenceDirectory=dependencies, Version=args.version, SourceRevisionId=args.source_commit,
          BaseIntermediateOutputPath=str(workdir / "obj/AtelierCapture") + "/",
          OutputPath=str(capture_output) + "/", AppendTargetFrameworkToOutputPath="false")
    shutil.copyfile(capture_output / "AtelierCapture.dll", stage / "BepInEx/plugins/AtelierCapture.dll")
    shutil.copyfile(ROOT / "README.md", stage / "README.md")
    shutil.copytree(ROOT / "Licenses", stage / "Licenses", dirs_exist_ok=True)
    (workdir / "dependencies.json").write_text(json.dumps({"source_commit": args.source_commit,
        "dependencies": [{"name": name, "url": url, "sha256": checksum}
                         for name, (url, checksum) in DEPENDENCIES.items()]}, indent=2) + "\n", encoding="utf-8")
    subprocess.run([sys.executable, str(ROOT / "scripts/package.py"), "--stage", str(stage),
                    "--output", str(args.output.resolve()), "--version", args.version], cwd=ROOT, check=True)
    print(f"Install ZIP: {args.output.resolve() / f'Resleriana-Capture-v{args.version}-win-x64.zip'}")


if __name__ == "__main__":
    main()
