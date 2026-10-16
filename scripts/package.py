"""Choose a release version and package built install files."""
import argparse
import configparser
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
UPDATE_TARGETS = ["BepInEx/plugins/AtelierCapture.dll", "BepInEx/plugins/AtelierCapture.Native.dll",
                  "BepInEx/plugins/capture-runtime.json"]
RECOVERY_CONTRACT = "capture5-checkpoint2"
UPDATER_FILES = {"BepInEx/patchers/AtelierCapture.Updater.dll", "dotnet/AtelierCapture.Update.exe",
                 "dotnet/AtelierCapture.Update.dll", "dotnet/AtelierCapture.Update.deps.json",
                 "dotnet/AtelierCapture.Update.runtimeconfig.json", "dotnet/hostfxr.dll"}
CONFIGURATION_VALUES = {
    "UnityBaseLibrariesSource": "2022.3.62.zip",
    "GlobalMetadataPath": "{GameDataPath}/il2cpp_data/Metadata/global-metadata.dat",
    "IL2CPPInteropAssembliesPath": "{BepInEx}",
}
CONFIGURATION_CONTRACT = [{"path": "BepInEx/config/BepInEx.cfg", "section": "IL2CPP", "key": key, "value": value}
                          for key, value in CONFIGURATION_VALUES.items()]
LOCALIZATION_CONFIG = "BepInEx/config/AtelierResleriana.Plugin.Localization.cfg"
CONFIGURATION_CONTRACT.append({"path": LOCALIZATION_CONFIG, "section": "Data", "key": "AutoUpdate", "value": "false"})
MUTABLE_CONFIG_FILES = {"BepInEx/config/BepInEx.cfg", LOCALIZATION_CONFIG}


def parse_version(value):
    match = re.fullmatch(r"v?(\d+)\.(\d+)\.(\d+)", value)
    if not match:
        raise ValueError(f"Invalid release version: {value}")
    return tuple(map(int, match.groups()))


def release_version():
    return parse_version((ROOT / "VERSION").read_text().strip())


def install_file(name):
    if not name or "\\" in name or name.startswith("/") or ":" in name:
        return False
    path = Path(name)
    if not path.parts or any(part in {".", ".."} for part in path.parts):
        return False
    if name in {".doorstop_version", "doorstop_config.ini", "winhttp.dll", "README.md", "capture-control.ps1"}:
        return True
    if path.parts[0] == "Licenses":
        return path.suffix in {".txt", ".md"}
    if path.parts[0] == "dotnet":
        return path.suffix in {".dll", ".exe", ".json", ".dat"} or path.name in {".version", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt"}
    if path.parent.as_posix() == "BepInEx/core":
        return path.suffix in {".dll", ".config"}
    if name == "BepInEx/patchers/AtelierCapture.Updater.dll":
        return True
    if name == "BepInEx/plugins/Resources/LocalizationData.json":
        return True
    if name in MUTABLE_CONFIG_FILES or name in {"BepInEx/unity-libs/2022.3.62.zip", "BepInEx/plugins/capture-runtime.json"}:
        return True
    return path.parent.as_posix() == "BepInEx/plugins" and path.suffix == ".dll" and (
        path.name in {"AtelierCapture.dll", "AtelierCapture.Native.dll"} or path.name.startswith("AtelierResleriana.")
        or path.name in {"K4os.Compression.LZ4.dll", "Universal.Common.dll"})


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_stage(stage):
    stage = stage.resolve()
    required = {"winhttp.dll", "doorstop_config.ini", "capture-control.ps1", "dotnet/coreclr.dll", "dotnet/System.Private.CoreLib.dll",
                "dotnet/System.Text.Json.dll", "dotnet/System.IO.Compression.dll",
                "BepInEx/core/BepInEx.Core.dll", "BepInEx/core/BepInEx.Unity.IL2CPP.dll",
                "BepInEx/core/Il2CppInterop.Runtime.dll", "BepInEx/core/Il2CppInterop.Common.dll",
                "BepInEx/core/Il2CppInterop.HarmonySupport.dll", "BepInEx/core/0Harmony.dll",
                "BepInEx/core/TerraFX.Interop.Windows.dll",
                "BepInEx/plugins/AtelierCapture.dll", "BepInEx/plugins/AtelierCapture.Native.dll", "BepInEx/plugins/capture-runtime.json",
                "BepInEx/plugins/Resources/LocalizationData.json", "BepInEx/config/BepInEx.cfg",
                "BepInEx/unity-libs/2022.3.62.zip", LOCALIZATION_CONFIG}
    files = []
    for path in stage.rglob("*"):
        if path.is_symlink() or not path.resolve().is_relative_to(stage):
            raise ValueError(f"Install stage contains an external link: {path}")
        if path.is_file():
            name = path.relative_to(stage).as_posix()
            if not install_file(name):
                raise ValueError(f"Unexpected install file: {name}")
            files.append(name)
    if not required.issubset(files):
        raise ValueError(f"Missing install files: {sorted(required - set(files))}")
    certificate = json.loads((stage / "BepInEx/plugins/capture-runtime.json").read_text())
    update_mode = certificate.get("update_mode")
    if update_mode not in {"automatic", "manual"}:
        raise ValueError("Install runtime certificate must identify the update mode")
    if update_mode == "automatic" and not UPDATER_FILES.issubset(files):
        raise ValueError("Automatic installer is missing updater components")
    if update_mode == "manual" and UPDATER_FILES.intersection(files):
        raise ValueError("Manual installer contains updater components")
    if certificate.get("schema") != 1 or certificate.get("runtime_major") != 10 or certificate.get("runtime_version") != "10.0.13":
        raise ValueError("Install runtime certificate must identify embedded .NET 10.0.13")
    parse_version(certificate["capture_version"])
    if certificate.get("recovery_contract") != RECOVERY_CONTRACT:
        raise ValueError("Capture update recovery contract missing or unsupported")
    client = certificate.get("supported_client", {})
    for key in ("executable_sha256", "gameassembly_sha256", "metadata_sha256"):
        if not re.fullmatch(r"[a-f0-9]{64}", client.get(key, "")):
            raise ValueError(f"Missing supported client fingerprint: {key}")
    covered = set()
    for entry in certificate["runtime_files"]:
        name = entry["path"]
        if name in covered or not install_file(name) or name == "BepInEx/plugins/capture-runtime.json":
            raise ValueError(f"Invalid runtime certificate path: {name}")
        path = (stage / name).resolve()
        if not path.is_relative_to(stage) or not path.is_file():
            raise ValueError(f"Missing runtime certificate file: {name}")
        if path.stat().st_size != entry["size"] or sha256(path) != entry["sha256"]:
            raise ValueError(f"Runtime certificate file mismatch: {name}")
        covered.add(name)
    expected = {name for name in files if not name.startswith("Licenses/") and name not in {"README.md", "BepInEx/plugins/capture-runtime.json"} and name not in MUTABLE_CONFIG_FILES}
    if covered != expected:
        raise ValueError("Runtime certificate does not cover the complete installed load graph")
    configuration = (stage / "doorstop_config.ini").read_text()
    if "coreclr_path = dotnet\\coreclr.dll" not in configuration or "corlib_dir = dotnet" not in configuration:
        raise ValueError("Doorstop must load the adjacent embedded runtime")
    for path in MUTABLE_CONFIG_FILES:
        config = configparser.ConfigParser(interpolation=None)
        config.read(stage / path, encoding="utf-8-sig")
        for entry in CONFIGURATION_CONTRACT:
            if entry["path"] == path and config.get(entry["section"], entry["key"], fallback="").strip() != entry["value"]:
                raise ValueError(f"Required offline configuration missing or changed: {path} {entry['key']}")
    contract = certificate.get("configuration_contract")
    if contract != CONFIGURATION_CONTRACT:
        raise ValueError("Runtime configuration contract missing or unsupported")
    return sorted(files)


def package_update(stage, output, version, signing_key):
    certificate = json.loads((stage / UPDATE_TARGETS[-1]).read_text())
    if certificate["capture_version"] != version:
        raise ValueError("Capture update version differs from runtime certificate")
    if certificate["update_mode"] == "manual":
        if signing_key is not None:
            raise ValueError("Manual-only packages cannot produce automatic updates")
        return None
    manifest = {"schema": 1, "version": version, "recovery_contract": RECOVERY_CONTRACT,
                "files": [{"path": name, "size": (stage / name).stat().st_size,
                           "sha256": sha256(stage / name)} for name in UPDATE_TARGETS]}
    path = output / f"Resleriana-Capture-v{version}-update-win-x64.zip"
    if signing_key is None:
        path.unlink(missing_ok=True)
        return None
    if signing_key.resolve().is_relative_to(ROOT):
        raise ValueError("The release private key must be kept outside the repository")
    with tempfile.TemporaryDirectory(prefix=".capture-update-", dir=output) as temporary:
        temporary = Path(temporary)
        manifest_path = temporary / "update.json"
        signature_path = temporary / "update.sig"
        manifest_path.write_bytes((json.dumps(manifest, indent=2) + "\n").encode("utf-8"))
        try:
            subprocess.run([str((stage / "dotnet/AtelierCapture.Update.exe").resolve()), "--sign",
                            str(manifest_path.resolve()), str(signature_path.resolve()), str(signing_key.resolve())],
                           check=True)
        except subprocess.CalledProcessError:
            raise ValueError("The updater refused to sign this release") from None
        candidate_path = temporary / "update.zip"
        with zipfile.ZipFile(candidate_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            archive.write(manifest_path, "update.json")
            archive.write(signature_path, "update.sig")
            for name in UPDATE_TARGETS:
                archive.write(stage / name, name)
        candidate_path.replace(path)
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version-only", action="store_true")
    parser.add_argument("--stage", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--signing-key", type=Path, help="External organizer PEM key; omit for a full installer without an automatic-update asset")
    args = parser.parse_args()
    version = ".".join(map(str, parse_version(args.version) if args.version else release_version()))
    outputs = {"version": version, "tag": f"v{version}"}
    if not args.version_only:
        if not args.stage or not args.output:
            parser.error("--stage and --output are required when packaging")
        files = validate_stage(args.stage)
        update_mode = json.loads((args.stage / UPDATE_TARGETS[-1]).read_text())["update_mode"]
        suffix = "-manual" if update_mode == "manual" else ""
        args.output.mkdir(parents=True, exist_ok=True)
        update_path = package_update(args.stage, args.output, version, args.signing_key)
        installer_suffix = "-AutoUpdater" if update_mode == "automatic" else ""
        archive_path = args.output / f"Resleriana-Capture-v{version}-win-x64{installer_suffix}.zip"
        with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            for name in files:
                archive.write(args.stage / name, name)
        plugin_path = args.stage / "BepInEx/plugins/AtelierCapture.dll"
        provenance = json.loads((args.stage.parent / "dependencies.json").read_text())
        manifest = {"schema": 2, "version": version, "update_mode": update_mode, "source_commit": provenance["source_commit"],
                    "source_digest": provenance["source_digest"], "sdk": provenance["sdk"], "runtime": provenance["runtime"],
                    "archive": {"name": archive_path.name, "sha256": sha256(archive_path)},
                    "update": {"name": update_path.name, "sha256": sha256(update_path)} if update_path else None,
                    "plugin": {"name": "AtelierCapture.dll", "sha256": sha256(plugin_path)},
                    "dependencies": provenance["dependencies"], "packages": provenance["packages"],
                    "source_files": provenance["source_files"], "vendor_sources": provenance["vendor_sources"], "native_observer": provenance["native_observer"],
                    "files": [{"path": name, "size": (args.stage / name).stat().st_size, "sha256": sha256(args.stage / name)} for name in files]}
        manifest_path = args.output / f"release-manifest{suffix}.json"
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        section = re.search(r"^## v?" + re.escape(version) + r"(?:[ \t]+[^\n]*)?\n(.*?)(?=^## |\Z)",
                            changelog, flags=re.MULTILINE | re.DOTALL)
        notes = section[1].strip() if section and section[1].strip() else "Local candidate; native/gameplay and hardware qualification status must be read from the delivery evidence.\n"
        (args.output / "release-notes.md").write_text(notes + "\n", encoding="utf-8")
        (args.output / f"SHA256SUMS{suffix}.txt").write_text(
            "".join(f"{sha256(path)}  {path.name}\n" for path in (archive_path, update_path, manifest_path) if path), encoding="utf-8")
        shutil.copyfile(plugin_path, args.output / f"AtelierCapture{suffix}.dll")
        outputs["asset"] = archive_path.name
        print(f"v{version}: {len(files)} install files, {archive_path.stat().st_size:,} bytes")
    else:
        print(version)
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write("".join(f"{key}={value}\n" for key, value in outputs.items()))


if __name__ == "__main__":
    main()
