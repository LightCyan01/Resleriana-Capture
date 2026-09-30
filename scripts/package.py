"""Package tracked install files and choose the next release version."""
import argparse
import hashlib
import os
from pathlib import Path
import re
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
        return True
    if path.parent.as_posix() == "dotnet":
        return path.suffix in {".dll", ".json"} or path.name == ".version"
    if path.parent.as_posix() == "BepInEx/core":
        return path.suffix in {".dll", ".config"}
    if name == "BepInEx/plugins/Resources/LocalizationData.json":
        return True
    return path.parent.as_posix() == "BepInEx/plugins" and path.suffix == ".dll" and (
        path.name == "AtelierCapture.dll" or path.name.startswith("AtelierResleriana.")
        or path.name in {"K4os.Compression.LZ4.dll", "Universal.Common.dll"})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    version = ".".join(map(str, release_version()))
    files = sorted(name for name in git("ls-files", "-z").split("\0") if name and install_file(name))
    required = {"winhttp.dll", "doorstop_config.ini", "dotnet/coreclr.dll",
                "BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/core/Il2CppInterop.HarmonySupport.dll",
                "BepInEx/plugins/AtelierCapture.dll", "BepInEx/plugins/Resources/LocalizationData.json"}
    if not required.issubset(files):
        raise ValueError(f"Missing install files: {sorted(required - set(files))}")
    args.output.mkdir(parents=True, exist_ok=True)
    archive_path = args.output / f"Resleriana-Capture-v{version}-win-x64.zip"
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name in files:
            archive.write(ROOT / name, name)
    with archive_path.open("rb") as stream:
        checksum = hashlib.file_digest(stream, "sha256").hexdigest()
    (args.output / "SHA256SUMS.txt").write_text(f"{checksum}  {archive_path.name}\n", encoding="utf-8")
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"tag=v{version}\nasset={archive_path.name}\n")
    print(f"v{version}: {len(files)} install files, {archive_path.stat().st_size:,} bytes")


if __name__ == "__main__":
    main()
