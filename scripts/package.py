"""Choose a release version and package built install files."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def parse_version(value):
    match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", value)
    if not match:
        raise ValueError(f"Invalid release version: {value}")
    return tuple(map(int, match.groups()))


def release_version():
    minimum = parse_version((ROOT / "VERSION").read_text().strip())
    tags = [t for t in git("tag", "--list", "v*").splitlines() if re.fullmatch(r"v\d+\.\d+\.\d+", t)]
    current = set(git("tag", "--points-at", "HEAD").splitlines())
    existing = [parse_version(t) for t in tags if t in current]
    if existing:
        return max(existing)
    if not tags:
        return minimum
    major, minor, patch = max(map(parse_version, tags))
    return max(minimum, (major, minor, patch + 1))


def install_file(name):
    path = Path(name)
    if name in {".doorstop_version", "doorstop_config.ini", "winhttp.dll", "README.md"}:
        return True
    if path.parts[0] == "Licenses":
        return path.suffix in {".txt", ".md"}
    if path.parent.as_posix() == "dotnet":
        return path.suffix in {".dll", ".json"} or path.name == ".version"
    if path.parent.as_posix() == "BepInEx/core":
        return path.suffix in {".dll", ".config"}
    if name == "BepInEx/plugins/Resources/LocalizationData.json":
        return True
    return path.parent.as_posix() == "BepInEx/plugins" and path.suffix == ".dll" and (
        path.name == "AtelierCapture.dll" or path.name.startswith("AtelierResleriana.")
        or path.name in {"K4os.Compression.LZ4.dll", "Universal.Common.dll"})


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version-only", action="store_true")
    parser.add_argument("--stage", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    version = ".".join(map(str, release_version()))
    outputs = {"version": version, "tag": f"v{version}"}
    if not args.version_only:
        if not args.stage or not args.output:
            parser.error("--stage and --output are required when packaging")
        files = sorted(path.relative_to(args.stage).as_posix() for path in args.stage.rglob("*")
                       if path.is_file() and install_file(path.relative_to(args.stage).as_posix()))
        required = {"winhttp.dll", "doorstop_config.ini", "dotnet/coreclr.dll",
                    "BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/core/Il2CppInterop.HarmonySupport.dll",
                    "BepInEx/plugins/AtelierCapture.dll", "BepInEx/plugins/Resources/LocalizationData.json"}
        if not required.issubset(files):
            raise ValueError(f"Missing install files: {sorted(required - set(files))}")
        args.output.mkdir(parents=True, exist_ok=True)
        archive_path = args.output / f"Resleriana-Capture-v{version}-win-x64.zip"
        with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in files:
                archive.write(args.stage / name, name)
        plugin_path = args.stage / "BepInEx/plugins/AtelierCapture.dll"
        provenance = json.loads((args.stage.parent / "dependencies.json").read_text())
        manifest = {"schema": 1, "version": version, "source_commit": provenance["source_commit"],
                    "archive": {"name": archive_path.name, "sha256": sha256(archive_path)},
                    "plugin": {"name": "AtelierCapture.dll", "sha256": sha256(plugin_path)},
                    "dependencies": provenance["dependencies"]}
        manifest_path = args.output / "release-manifest.json"
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        section = re.search(r"^## v?" + re.escape(version) + r"(?:[ \t]+[^\n]*)?\n(.*?)(?=^## |\Z)",
                            changelog, flags=re.MULTILINE | re.DOTALL)
        if not section or not section[1].strip():
            raise ValueError(f"CHANGELOG.md has no release notes for {version}")
        (args.output / "release-notes.md").write_text(section[1].strip() + "\n", encoding="utf-8")
        (args.output / "SHA256SUMS.txt").write_text(
            "".join(f"{sha256(path)}  {path.name}\n" for path in (archive_path, manifest_path)), encoding="utf-8")
        shutil.copyfile(plugin_path, args.output / "AtelierCapture.dll")
        outputs["asset"] = archive_path.name
        print(f"v{version}: {len(files)} install files, {archive_path.stat().st_size:,} bytes")
    else:
        print(version)
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write("".join(f"{key}={value}\n" for key, value in outputs.items()))


if __name__ == "__main__":
    main()
