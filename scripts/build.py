"""Build the pinned Windows runtime and recorder from public workspace inputs."""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from package import CONFIGURATION_CONTRACT, CONFIGURATION_VALUES, LOCALIZATION_CONFIG, MUTABLE_CONFIG_FILES, ROOT, install_file, parse_version, sha256, validate_stage

BEPINEX_COMMIT = "5b766a3b7f6c164d4798924a93f3acf4db769d06"
INTEROP_COMMIT = "dbda1cb353b0f4253345dc45136d170b9e50a5a0"
SCANNER_COMMIT = "81a6f78c8b653e0da4a3420ac4cd00819e8b6292"
SDK_VERSION = "10.0.402"
RUNTIME_VERSION = "10.0.13"
BOOTSTRAP_EXTENSIONS = ("Primitives", "DependencyInjection.Abstractions", "DependencyInjection",
                        "Logging.Abstractions", "Options", "Logging")
DEPENDENCIES = {
    "bepinex.zip": (
        "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip",
        "sha256", "f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a"),
    "localization.zip": (
        "https://github.com/ongandrew/AtelierResleriana/releases/download/2.0.0/AtelierResleriana.Plugin.Localization-2.0.0.zip",
        "sha256", "3b56e6ecaa413fbc309442adc9f55c93005efe362238ee61d3d8db0349130da0"),
    "dotnet-runtime-10.0.13.zip": (
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.zip",
        "sha512", "2b98792ce86adc50899764ad2e4d21bd25cfda5432eeb0b4dfe7944f3ea049f9e0570a8f4aee92314d97cdb5f8b7be1363d501fc5012e680371e607af648ef40"),
    "2022.3.62.zip": (
        "https://unity.bepinex.dev/libraries/2022.3.62.zip",
        "sha256", "575e7d600f69de8200ccf4db700b3ae6252366c22e8c3434c860e428974518d1"),
    "Unity.InputSystem.dll": (
        f"https://raw.githubusercontent.com/BepInEx/BepInEx/{BEPINEX_COMMIT}/Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/Unity.InputSystem.dll",
        "sha256", "daffaa004643904cdf61b6a399b703edb7c8b9c599257fa835c82bb7cee47e79"),
    "UnityEngine.CoreModule.dll": (
        f"https://raw.githubusercontent.com/BepInEx/BepInEx/{BEPINEX_COMMIT}/Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/UnityEngine.CoreModule.dll",
        "sha256", "4bcc37f551deec1d85256cb5941241724680fbe98a94b19b29ec6f92dc7720d6"),
    "UnityEngine.InputLegacyModule.dll": (
        f"https://raw.githubusercontent.com/BepInEx/BepInEx/{BEPINEX_COMMIT}/Runtimes/Unity/BepInEx.Unity.IL2CPP/lib/UnityEngine.InputLegacyModule.dll",
        "sha256", "a888a2929261dd2cf764264f5bd697aa9251525bf4a5491743addea5e989066c"),
    "Il2Cppmscorlib.dll": (
        f"https://raw.githubusercontent.com/BepInEx/Il2CppInterop/{INTEROP_COMMIT}/Il2CppInterop.Runtime/Libs/Il2Cppmscorlib.dll",
        "sha256", "ee6176648f56660e32dc354a9063197ee2c2022f8ec8df3b177516d0fe671948"),
}
SDK_ARCHIVE = (
    "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-win-x64.zip",
    "sha512", "ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd")
SUPPORTED_CLIENT = {
    "executable_sha256": "6fdcb1607398bfd58c6f83ec0908781e82c65c671376e9d1444001c55c75731a",
    "gameassembly_sha256": "35868ee52cb199b018e2ced962dd233a9f4d200a7e5d8e9903e7f4607f21f5ea",
    "metadata_sha256": "af138395b956bc835f99b2fd140a4c5015781e56c9cab3052bdb7e69065cc9b0",
    "unity_version": "2022.3.62f2", "game_version": "2.16.0",
}
IL2CPP_CONFIGURATION = "[IL2CPP]\n" + "".join(f"{key} = {value}\n" for key, value in CONFIGURATION_VALUES.items())
LOCALIZATION_PATCH = {
    "name": "PF17-preserve-untouched-CAB",
    "path": "BepInEx/plugins/AtelierResleriana.Plugin.Localization.dll",
    "original_sha256": "0b590b78f82cf50f5c3950a447d3f19e1333b2f8c76c8ee662c15104b5f4fec0",
    "modified_sha256": "a53a51c19f2c4a0a1c8d26a181d49c09f3e524b583f96641b1294768ccd494cf",
    "assembly_version": "2.0.0.0",
    "method": "AtelierResleriana.Plugin.Localization.Plugin::AssetBundleLoadFromStreamPrefix(Il2CppSystem.Object&)",
    "method_token": "0x06000041", "method_rva": 0x2C70,
    "il_offset": 0x1FE, "original_target": 0x470, "modified_target": 0x440,
    "file_operand_offset": 0x107B,
}


def patch_localization(data):
    if hashlib.sha256(data).hexdigest() != LOCALIZATION_PATCH["original_sha256"]:
        raise ValueError("Localization PF17 requires the exact unmodified 2.0.0 dependency")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    section_count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    body = None
    for index in range(section_count):
        section = pe + 24 + optional_size + index * 40
        _, address, size, pointer = struct.unpack_from("<IIII", data, section + 8)
        if address <= LOCALIZATION_PATCH["method_rva"] < address + size:
            body = pointer + LOCALIZATION_PATCH["method_rva"] - address
            break
    if body is None or struct.unpack_from("<H", data, body)[0] >> 12 != 3:
        raise ValueError("Localization PF17 method header changed")
    code = body + 12
    site = code + LOCALIZATION_PATCH["il_offset"]
    operand = site + 1
    following = LOCALIZATION_PATCH["il_offset"] + 5
    if (operand != LOCALIZATION_PATCH["file_operand_offset"]
            or struct.unpack_from("<I", data, body + 4)[0] != 1345
            or data[site] != 0x39
            or following + struct.unpack_from("<i", data, operand)[0] != LOCALIZATION_PATCH["original_target"]):
        raise ValueError("Localization PF17 branch contract changed")
    patched = bytearray(data)
    # Both targets are in the same EH regions; the existing copy block takes an empty stack.
    struct.pack_into("<i", patched, operand, LOCALIZATION_PATCH["modified_target"] - following)
    if (len(patched) != len(data) or patched[:operand] != data[:operand]
            or patched[operand + 4:] != data[operand + 4:]
            or hashlib.sha256(patched).hexdigest() != LOCALIZATION_PATCH["modified_sha256"]):
        raise ValueError("Localization PF17 output contract changed")
    return bytes(patched)


def owned_output(path):
    path = path.resolve()
    work = (ROOT / "work").resolve()
    if path == work or not path.is_relative_to(work):
        raise ValueError("Build outputs must be named directories inside this workspace's work folder")
    return path


def download(name, directory, definition, offline=False):
    url, algorithm, expected = definition
    path = directory / name
    if path.exists():
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, algorithm).hexdigest() == expected:
                return path
    if offline:
        raise ValueError(f"Missing or corrupt offline dependency: {name}")
    directory.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".partial")
    # The public Unity service rejects the default Python client user agent.
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    with urllib.request.urlopen(request, timeout=60) as response, temporary.open("wb") as output:
        shutil.copyfileobj(response, output)
    with temporary.open("rb") as stream:
        if hashlib.file_digest(stream, algorithm).hexdigest() != expected:
            raise ValueError(f"Dependency checksum mismatch: {name}")
    temporary.replace(path)
    return path


def extract(archive_path, stage, prefix="", install_only=False, plugins_only=False):
    with zipfile.ZipFile(archive_path) as archive:
        seen = set()
        for entry in archive.infolist():
            name = entry.filename.replace("\\", "/")
            if entry.is_dir() or not name.startswith(prefix):
                continue
            if plugins_only and not name.startswith("BepInEx/plugins/"):
                continue
            relative = name[len(prefix):]
            if not relative or (install_only and not install_file(relative)):
                continue
            destination = (stage / relative).resolve()
            if not destination.is_relative_to(stage.resolve()) or destination == stage.resolve():
                raise ValueError(f"Unsafe dependency archive path: {name}")
            if destination.as_posix().casefold() in seen:
                raise ValueError(f"Duplicate dependency archive path: {name}")
            seen.add(destination.as_posix().casefold())
            if (entry.external_attr >> 16) & 0o170000 == 0o120000:
                raise ValueError(f"Dependency archive symlink: {name}")
            destination.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, destination.open("wb") as output:
                shutil.copyfileobj(source, output)


def source_inventory(runtime_only=False):
    folders = [ROOT / "Sources/BepInEx", ROOT / "Sources/Il2CppInterop", ROOT / "Sources/AtelierCapture.Native", ROOT / "Licenses", ROOT / ".config"]
    if not runtime_only:
        folders.append(ROOT / "Sources/AtelierCapture")
    files = {ROOT / "VERSION", ROOT / "global.json", ROOT / "README.md", ROOT / "CHANGELOG.md",
             ROOT / "scripts/build.py", ROOT / "scripts/package.py"}
    files.add(ROOT / ".github/workflows/release.yml")
    for folder in folders:
        for path in folder.rglob("*"):
            if path.is_file() and not {"bin", "obj", "node_modules", ".store"}.intersection(path.relative_to(folder).parts):
                if path.suffix in {".cs", ".cpp", ".h", ".inc", ".csproj", ".props", ".targets", ".json", ".config", ".cfg", ".txt", ".md", ".ini", ".ps1"}:
                    files.add(path)
    return [{"path": path.relative_to(ROOT).as_posix(), "sha256": sha256(path), "size": path.stat().st_size}
            for path in sorted(files)]


def digest_inventory(files):
    return hashlib.sha256(json.dumps(files, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def native_catalog():
    abi = json.loads((ROOT / "Sources/AtelierCapture.Native/abi.json").read_text(encoding="utf-8"))
    contracts = json.loads((ROOT / "Sources/AtelierCapture/BattleHooks.json").read_text(encoding="utf-8"))
    contracts += json.loads((ROOT / "Sources/AtelierCapture/Gameplay/AtelierHooks.json").read_text(encoding="utf-8"))["hooks"]
    contracts += abi["additional_contracts"]
    def storage(name, result=False):
        if name.endswith("&"):
            return "b" + storage(name[:-1])
        item = abi["types"].get(name)
        if name == "System.Void":
            return "v"
        if item is None:
            return "p"
        kind, size = item["kind"], item["size"]
        if kind == "f32":
            return "f4"
        if kind == "f64":
            return "f8"
        if kind in {"nullable_i32", "nullable_i64", "cancellation_token", "unitask"}:
            return ("s" if result else "d") + str(size)
        return "i" + str(size)
    found = {}
    for item in contracts:
        identity = item["type"] + "." + item["method"] + "(" + ",".join(item["parameters"]) + ")"
        receiver = "static" if item["is_static"] else "value" if item["type"] in abi["value_receivers"] else "reference"
        result = storage(item["returns"], True)
        parameters = [storage(p) for p in item["parameters"]]
        shape = result + ":" + receiver + ":" + ",".join(parameters)
        found[identity] = {"identity": identity, "shape": shape, "result": result, "receiver": receiver, "parameters": parameters}
    return {"schema": 1, "types": abi["types"], "contracts": sorted(found.values(), key=lambda x: x["identity"])}


def build_native(workdir):
    directory = owned_output(workdir / "native")
    directory.mkdir(parents=True, exist_ok=True)
    catalog = native_catalog()
    rows = catalog["contracts"]
    def ctype(code):
        if code == "v": return "void"
        if code == "f4": return "float"
        if code == "f8": return "double"
        if code.startswith("i") or (code.startswith("d") and int(code[1:]) in (1, 2, 4, 8)):
            return "std::uint" + str(int(code[1:]) * 8) + "_t"
        return "void*"
    code = [f"static constexpr std::size_t entry_count = {len(rows)};", "static Binding bindings[entry_count];"]
    for index, row in enumerate(rows):
        aggregate = row["result"].startswith("s") and int(row["result"][1:]) not in (1, 2, 4, 8)
        result = "void*" if aggregate else ctype(row["result"].replace("s", "d", 1))
        parts = (["p"] if aggregate else []) + ([] if row["receiver"] == "static" else ["p"]) + row["parameters"] + ["p"]
        types = [ctype(p) for p in parts]
        names = [f"a{i}" for i in range(len(parts))]
        offset = int(aggregate) + int(row["receiver"] != "static")
        cells = ["&" + names[offset + i] for i in range(len(row["parameters"]))]
        params = ", ".join(t + " " + n for t, n in zip(types, names))
        code.append(f"static {result} __cdecl entry_{index}({params}) {{")
        code.append("const void* arguments[] = {" + ",".join(cells or ["nullptr"]) + "};")
        receiver = "nullptr" if row["receiver"] == "static" else names[int(aggregate)]
        code.append(f"CaptureFrame frame{{sizeof(CaptureFrame), 1, CapturePhase::Entering, {len(cells)}, {index}, {receiver}, {names[-1]}, arguments, nullptr, 0}};")
        args = ", ".join(names)
        call = f"reinterpret_cast<{result}(__cdecl*)({', '.join(types)})>(original)({args})"
        code.append(f"return invoke<{result}>(bindings[{index}], frame, [&](void* original) -> {result} {{ return {call}; }}" + (", a0);" if aggregate else ");"))
        code.append("}")
    code.append("static void* entries[] = {" + ",".join(f"reinterpret_cast<void*>(&entry_{i})" for i in range(len(rows))) + "};")
    for field in ("identity", "shape"):
        code.append(f"static const char* {'identities' if field == 'identity' else 'shapes'}[] = {{" + ",".join(json.dumps(r[field]) for r in rows) + "};")
    catalog_text = json.dumps(catalog, separators=(",", ":"))
    code.append("static const char catalog_json[] =\n" + "\n".join(json.dumps(catalog_text[i:i+2048]) for i in range(0, len(catalog_text), 2048)) + ";")
    (directory / "entries.inc").write_text("\n".join(code) + "\n", encoding="utf-8")
    (directory / "catalog.json").write_text(json.dumps(catalog, indent=2) + "\n", encoding="utf-8")
    vswhere = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Microsoft Visual Studio/Installer/vswhere.exe"
    vs = subprocess.check_output([str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], text=True).strip()
    if not vs:
        raise ValueError("MSVC x64 build tools are required for the finite native observer")
    environment_script = directory / "compiler-environment.cmd"
    developer_command = Path(vs) / "Common7/Tools/VsDevCmd.bat"
    environment_script.write_text(f'@echo off\ncall "{developer_command}" -no_logo -arch=x64 -host_arch=x64 >nul\nif errorlevel 1 exit /b %errorlevel%\nset\n', encoding="utf-8")
    variables = subprocess.check_output('cmd.exe /d /s /c ""' + str(environment_script) + '""', text=True)
    environment = os.environ.copy()
    environment.update(line.split("=", 1) for line in variables.splitlines() if "=" in line and not line.startswith("="))
    compiler = Path(environment["VCToolsInstallDir"]) / "bin/Hostx64/x64/cl.exe"
    linker = compiler.with_name("link.exe")
    output = directory / "AtelierCapture.Native.dll"
    arguments = [str(compiler), "/nologo", "/std:c++20", "/EHs", "/MT", "/O2", "/W4", "/WX", "/guard:cf", "/Brepro", "/LD",
                 "/I" + str(directory), "/Fo" + str(directory) + "/", str(ROOT / "Sources/AtelierCapture.Native/bridge.cpp"),
                 "/link", "/OUT:" + str(output), "/INCREMENTAL:NO", "/OPT:NOICF", "/DYNAMICBASE", "/NXCOMPAT", "/Brepro", "Synchronization.lib"]
    subprocess.run(arguments, cwd=directory, env=environment, check=True)
    dumpbin = compiler.with_name("dumpbin.exe")
    imports = subprocess.check_output([str(dumpbin), "/imports", str(output)], text=True)
    imported_names = sorted(set(re.findall(r"^\s+([A-Za-z0-9_.-]+\.dll)\s*$", imports, re.MULTILINE)))
    if any(name.casefold() != "kernel32.dll" and not name.casefold().startswith("api-ms-win-core-") for name in imported_names):
        raise ValueError(f"Native observer has an unbundled dependency: {imported_names}")
    exports = subprocess.check_output([str(dumpbin), "/exports", str(output)], text=True)
    if "capture_test_pause" in exports:
        raise ValueError("Instrumented native checks must not ship in the observer")
    (directory / "imports.txt").write_text(imports, encoding="utf-8")
    (directory / "exports.txt").write_text(exports, encoding="utf-8")
    provenance = {"compiler_version": environment.get("VCToolsVersion"), "compiler_sha256": sha256(compiler), "linker_sha256": sha256(linker),
                  "windows_sdk": environment.get("WindowsSDKVersion"), "arguments": [a.replace(str(ROOT), "<workspace>") for a in arguments[1:]],
                  "generated_catalog_sha256": sha256(directory / "catalog.json"), "generated_source_sha256": sha256(directory / "entries.inc"),
                  "output_sha256": sha256(output), "slots": len(rows), "imports": imported_names, "instrumented": False}
    (directory / "native-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    build_native_checks(directory, compiler, environment, rows)
    return output, provenance


def build_native_checks(directory, compiler, environment, rows):
    def ctype(code):
        if code == "v": return "void"
        if code in ("f4", "f8"): return "float" if code == "f4" else "double"
        if code.startswith("i") or code.startswith("d") and int(code[1:]) in (1, 2, 4, 8):
            return "std::uint" + str(int(code[1:]) * 8) + "_t"
        return "void*"
    bodies, runs = [], []
    for index, row in enumerate(rows):
        special = native_typed_fixture(row, index)
        if special is not None:
            body, run = special
            bodies.extend(body)
            runs.extend(run)
            continue
        aggregate = row["result"].startswith("s") and int(row["result"][1:]) not in (1, 2, 4, 8)
        result = "void*" if aggregate else ctype(row["result"].replace("s", "d", 1))
        codes = (["p"] if aggregate else []) + ([] if row["receiver"] == "static" else ["p"]) + row["parameters"] + ["p"]
        types, names = [ctype(c) for c in codes], [f"a{i}" for i in range(len(codes))]
        first = int(aggregate) + int(row["receiver"] != "static")
        receiver = "nullptr" if row["receiver"] == "static" else names[int(aggregate)]
        values = (["output_memory + 1"] if aggregate else []) + ([] if row["receiver"] == "static" else ["receiver_memory"])
        for p, code in enumerate(row["parameters"]):
            values.append(f"argument_memory[{p}]" if ctype(code) == "void*" else f"static_cast<{ctype(code)}>({1 if code == 'i1' else 0x30+p})")
        values.append("expected_method")
        signature = ",".join(t + " " + n for t, n in zip(types, names))
        body = [f"__declspec(noinline) static {result} __cdecl original_{index}({signature}) {{", f"original_begin({receiver}, {names[-1]});"]
        for p, code in enumerate(row["parameters"]):
            body.append(f"require({names[first+p]} == {values[first+p]});")
            if code.startswith("b"):
                body.append(f"++*static_cast<std::uint32_t*>({names[first+p]});")
        if aggregate:
            body += ["auto output = static_cast<std::uint64_t*>(a0);", f"output[0] = {index+1}; output[1] = 0x1234; return a0;"]
        elif result != "void": body.append(f"return {'argument_memory[15]' if result == 'void*' else f'static_cast<{result}>({index%100+1})'};")
        body.append("}")
        bodies += body
        patch = row["identity"] == "BattleServerControl.SyncActionStart()"
        preparation = [f"void* gateway = reinterpret_cast<void*>(&original_{index});"]
        if patch: preparation += [f"require(prepare(reinterpret_cast<void*>(&original_{index}), capture_entry(index), &gateway) == 0);"]
        runs += ["{", f"std::fprintf(stderr, \"native finite fixture slot {index}\\n\");", f"const auto index = capture_find({json.dumps(row['identity'])}, {json.dumps(row['shape'])}); require(index == {index});", *preparation,
                 f"require(capture_bind(index, reinterpret_cast<void*>(&original_{index}), gateway, callback) == 0);"]
        if patch: runs += [f"require(commit(reinterpret_cast<void*>(&original_{index})) == 0);"]
        runs += [f"using Call = {result}(__cdecl*)({','.join(types)});", "auto function = reinterpret_cast<Call>(capture_entry(index));",
                 "auto before = calls.load(); output_memory[0] = 0xABCD; output_memory[3] = 0xDCBA; SetLastError(0x2222);",
                 ("auto result = " if result != "void" else "") + "function(" + ",".join(values) + ");",
                 "require(GetLastError() == 0x4321 && _mm_getcsr() == 0x5F80); require(calls.load() == before + 1);",
                 "require(output_memory[0] == 0xABCD && output_memory[3] == 0xDCBA);"]
        if aggregate: runs.append(f"require(result == output_memory + 1 && output_memory[1] == {index+1} && output_memory[2] == 0x1234);")
        elif result != "void": runs.append("require(result == " + ("argument_memory[15]" if result == "void*" else f"static_cast<{result}>({index%100+1})") + ");")
        runs += ["should_throw = true; before = calls.load(); auto destroyed_before = destructors.load(); bool caught{};",
                 "try { SetLastError(0x2222); function(" + ",".join(values) + "); } catch (const FixtureException& error) { caught = true; require(error.identity == fault_identity && &error == fixture_exception_address()); }",
                 "should_throw = false; require(caught && calls.load() == before + 1 && destructors.load() == destroyed_before + 1);", "}"]
    bodies += [f"static constexpr std::size_t source_count = {len(rows)};", "static void* sources[] = {" + ",".join(f"reinterpret_cast<void*>(&original_{i})" for i in range(len(rows))) + "};",
               "static const char* identities_for_checks[] = {" + ",".join(json.dumps(r['identity']) for r in rows) + "};",
               "static void run_entries(CaptureCallback callback, DobbyPrepare prepare, DobbyAction commit) {", *runs, "}"]
    (directory / "fixture-entries.inc").write_text("\n".join(bodies) + "\n", encoding="utf-8")
    source = ROOT / "Sources/AtelierCapture.Native"
    common = [str(compiler), "/nologo", "/std:c++20", "/EHs", "/O2", "/W4", "/WX", "/LD", "/Fo"+str(directory)+"/", "/I"+str(directory)]
    subprocess.run(common + ["/MD", str(source / "thrower.cpp"), "/link", "/OUT:"+str(directory / "AtelierCapture.Native.Thrower.dll"), "/IMPLIB:"+str(directory / "thrower.lib")], cwd=directory, env=environment, check=True)
    subprocess.run(common + ["/MT", str(source / "checks.cpp"), "/link", "/OUT:"+str(directory / "AtelierCapture.Native.Checks.dll"), str(directory / "thrower.lib"), str(directory / "bridge.lib"), "/OPT:NOICF"], cwd=directory, env=environment, check=True)
    subprocess.run(common + ["/MT", "/DCAPTURE_NATIVE_CHECKS", str(source / "bridge.cpp"), "/link", "/OUT:"+str(directory / "AtelierCapture.Native.Races.dll"), "/IMPLIB:"+str(directory / "races.lib"), "Synchronization.lib"], cwd=directory, env=environment, check=True)
    subprocess.run(common + ["/MT", "/DCAPTURE_NATIVE_CHECKS", str(source / "checks.cpp"), "/link", "/OUT:"+str(directory / "AtelierCapture.Native.RaceChecks.dll"), str(directory / "thrower.lib"), str(directory / "races.lib"), "/OPT:NOICF"], cwd=directory, env=environment, check=True)


def native_typed_fixture(row, index):
    identity = row["identity"]
    if identity.startswith("BattleStateVariables.StartWave("):
        result, parameters = "NativeTask", "void* self, CancellationToken token, void* method"
        arguments = "receiver_memory, CancellationToken{argument_memory[0]}, expected_method"
        call_types = "void*,CancellationToken,void*"
        body = ["original_begin(self, method); require(token.source == argument_memory[0]); return NativeTask{argument_memory[15], -1234};"]
        check = "require(result.source == argument_memory[15] && result.token == -1234);"
    elif identity.startswith("BattleDamageControl.AIMCFGLOFGB("):
        result, parameters = "std::int64_t", "void* self, void* context, Nullable64 value, void* method"
        arguments = "receiver_memory, argument_memory[0], Nullable64{true, -0x123456781234567LL}, expected_method"
        call_types = "void*,void*,Nullable64,void*"
        body = ["original_begin(self, method); require(context == argument_memory[0]); require(value.hasValue && value.value == -0x123456781234567LL); return value.value;"]
        check = "require(result == -0x123456781234567LL);"
    elif identity.startswith("BattleDamageControl.OnFlagBurstGauge("):
        result, parameters = "void", "void* self, Nullable32 value, std::int32_t* flags, void* method"
        arguments = "receiver_memory, Nullable32{true, -123456789}, reinterpret_cast<std::int32_t*>(argument_memory[1]), expected_method"
        call_types = "void*,Nullable32,std::int32_t*,void*"
        body = ["original_begin(self, method); require(value.hasValue && value.value == -123456789); require(flags == reinterpret_cast<std::int32_t*>(argument_memory[1])); *flags = value.value;"]
        check = "require(*reinterpret_cast<std::int32_t*>(argument_memory[1]) == -123456789);"
    elif identity.startswith("BattleStateVariables.ItemSkillExec("):
        result, parameters = "NativeTask", "void* self, CancellationToken token, void* first, void* second, std::int32_t kind, void* text, std::int64_t value, bool flag, void* method"
        arguments = "receiver_memory, CancellationToken{argument_memory[0]}, argument_memory[1], argument_memory[2], -123, argument_memory[4], -0x123456781234567LL, true, expected_method"
        call_types = "void*,CancellationToken,void*,void*,std::int32_t,void*,std::int64_t,bool,void*"
        body = ["original_begin(self, method); require(token.source == argument_memory[0] && first == argument_memory[1] && second == argument_memory[2]); require(kind == -123 && text == argument_memory[4] && value == -0x123456781234567LL && flag); return NativeTask{argument_memory[15], -2345};"]
        check = "require(result.source == argument_memory[15] && result.token == -2345);"
    else:
        return None
    declaration = [f"__declspec(noinline) static {result} __cdecl original_{index}({parameters}) {{", *body, "}"]
    call = "function(" + arguments + ");"
    runs = ["{", f"std::fprintf(stderr, \"native real-POD fixture slot {index}\\n\");",
            f"const auto index = capture_find({json.dumps(identity)}, {json.dumps(row['shape'])}); require(index == {index});",
            f"require(capture_bind(index, reinterpret_cast<void*>(&original_{index}), reinterpret_cast<void*>(&original_{index}), callback) == 0);",
            f"using Call = {result}(__cdecl*)({call_types}); auto function = reinterpret_cast<Call>(capture_entry(index));",
            "auto before = calls.load(); SetLastError(0x2222);", ("auto result = " if result != "void" else "") + call,
            "require(GetLastError() == 0x4321 && calls.load() == before + 1);", check,
            "should_throw = true; before = calls.load(); auto destroyed_before = destructors.load(); bool caught{};",
            "try { SetLastError(0x2222); " + call + " } catch(const FixtureException& error) { caught = true; require(error.identity == fault_identity && &error == fixture_exception_address()); }",
            "should_throw = false; require(caught && calls.load() == before + 1 && destructors.load() == destroyed_before + 1);", "}"]
    return declaration, runs


def nuget_inventory(packages):
    result = []
    for archive in sorted(packages.glob("*/*/*.nupkg")):
        with zipfile.ZipFile(archive) as package:
            metadata = ET.fromstring(package.read(next(n for n in package.namelist() if n.endswith(".nuspec"))))
            fields = {node.tag.rsplit("}", 1)[-1]: node for node in metadata.iter()}
            repository = fields.get("repository")
        actual_sha512 = base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()
        expected = archive.with_suffix(archive.suffix + ".sha512").read_text().strip()
        if actual_sha512 != expected:
            raise ValueError(f"NuGet cache content hash mismatch: {archive.name}")
        package_metadata = json.loads((archive.parent / ".nupkg.metadata").read_text())
        result.append({"name": fields["id"].text, "version": fields["version"].text,
                       "sha256": sha256(archive), "sha512": actual_sha512, "size": archive.stat().st_size,
                       "source": package_metadata["source"],
                       "repository": repository.attrib if repository is not None else {}})
    return result


def write_offline_config(config, feed):
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local", value=feed.as_posix())
    ET.ElementTree(configuration).write(config, encoding="utf-8", xml_declaration=True)


def build(dotnet, project, workdir, offline, update_locks, **properties):
    packages = workdir / "packages"
    config = ROOT / ".config/nuget.config"
    if offline:
        config = workdir / "nuget.offline.config"
        feed = workdir / "offline-feed"
        feed.mkdir(exist_ok=True)
        for package in packages.glob("*/*/*.nupkg"):
            shutil.copyfile(package, feed / package.name)
        write_offline_config(config, feed)
    props = [f"-p:{name}={value}" for name, value in properties.items()]
    props += ["-p:GeneratePackageOnBuild=false", "-p:ContinuousIntegrationBuild=true", f"-p:PathMap={ROOT}=/_/Resleriana-Capture"]
    environment = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(workdir / "cli"),
                       DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
                       DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_NOLOGO="1",
                       NUGET_PACKAGES=str(packages), NUGET_HTTP_CACHE_PATH=str(workdir / "http-cache"))
    restore = [str(dotnet), "restore", str(project), "--configfile", str(config), "--packages", str(packages)]
    restore.append("--force-evaluate" if update_locks else "--locked-mode")
    subprocess.run([*restore, *props], cwd=ROOT, env=environment, check=True)
    subprocess.run([str(dotnet), "build", str(project), "-c", "Release", "--no-restore", "--nologo", "--disable-build-servers", *props], cwd=ROOT, env=environment, check=True)


def self_check():
    with tempfile.TemporaryDirectory(prefix="build-check-", dir=ROOT / "work/checks") as temporary:
        root = Path(temporary)
        for name in ("../escape.dll", "/absolute.dll", "safe.dll", "a/../../escape.dll"):
            archive = root / "case.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr(name, b"synthetic")
            try:
                extract(archive, root / "stage")
                if name != "safe.dll":
                    raise AssertionError(f"Unsafe archive path accepted: {name}")
            except ValueError:
                if name == "safe.dll":
                    raise
        feed = root / 'feed & "quoted" <dir>'
        write_offline_config(root / "nuget.offline.config", feed)
        if ET.parse(root / "nuget.offline.config").find("packageSources/add").get("value") != feed.as_posix():
            raise AssertionError("Offline feed path was not escaped")
        archive = root / "localization.zip"
        with zipfile.ZipFile(archive, "w") as output:
            output.writestr("dotnet/coreclr.dll", b"old runtime must never overlay")
            output.writestr("BepInEx/plugins/AtelierResleriana.Synthetic.dll", b"plugin")
        extract(archive, root / "plugins", install_only=True, plugins_only=True)
        if (root / "plugins/dotnet/coreclr.dll").exists():
            raise AssertionError("Localization overwrote embedded runtime")
        if not (root / "plugins/BepInEx/plugins/AtelierResleriana.Synthetic.dll").exists():
            raise AssertionError("Localization plugin was omitted")
        try:
            owned_output(ROOT.parent / "foreign")
            raise AssertionError("Foreign build output accepted")
        except ValueError:
            pass
        try:
            validate_stage(root / "stage")
            raise AssertionError("Incomplete runtime accepted")
        except ValueError:
            pass
        stage = root / "complete-stage"
        names = {"winhttp.dll", "doorstop_config.ini", "capture-control.ps1", "dotnet/coreclr.dll",
                 "dotnet/System.Private.CoreLib.dll", "dotnet/System.Text.Json.dll", "dotnet/System.IO.Compression.dll",
                 "BepInEx/core/BepInEx.Core.dll", "BepInEx/core/BepInEx.Unity.IL2CPP.dll", "BepInEx/core/0Harmony.dll",
                 "BepInEx/core/Il2CppInterop.Runtime.dll", "BepInEx/core/Il2CppInterop.Common.dll",
                 "BepInEx/core/Il2CppInterop.HarmonySupport.dll", "BepInEx/plugins/AtelierCapture.dll", "BepInEx/plugins/AtelierCapture.Native.dll",
                 "BepInEx/core/TerraFX.Interop.Windows.dll",
                 "BepInEx/plugins/Resources/LocalizationData.json", "BepInEx/config/BepInEx.cfg",
                 "BepInEx/unity-libs/2022.3.62.zip", LOCALIZATION_CONFIG}
        for name in names:
            path = stage / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b"synthetic")
        (stage / "doorstop_config.ini").write_text("coreclr_path = dotnet\\coreclr.dll\ncorlib_dir = dotnet\n")
        (stage / "BepInEx/config/BepInEx.cfg").write_text(IL2CPP_CONFIGURATION)
        (stage / LOCALIZATION_CONFIG).write_text("[Data]\nAutoUpdate = false\n")
        records = [{"path": name, "sha256": sha256(stage / name), "size": (stage / name).stat().st_size}
                   for name in sorted(names) if name not in MUTABLE_CONFIG_FILES]
        certificate = {"schema": 1, "runtime_major": 10, "runtime_version": RUNTIME_VERSION,
                       "runtime_files": records, "supported_client": SUPPORTED_CLIENT, "configuration_contract": CONFIGURATION_CONTRACT}
        certificate_path = stage / "BepInEx/plugins/capture-runtime.json"
        certificate_path.write_text(json.dumps(certificate))
        if len(validate_stage(stage)) != len(names) + 1:
            raise AssertionError("Complete stage inventory changed")
        (stage / "BepInEx/config/BepInEx.cfg").write_text(IL2CPP_CONFIGURATION + "[Logging.Console]\nEnabled = false\n")
        validate_stage(stage)
        for key, value in CONFIGURATION_VALUES.items():
            (stage / "BepInEx/config/BepInEx.cfg").write_text(IL2CPP_CONFIGURATION.replace(f"{key} = {value}", f"{key} = foreign-input"))
            try:
                validate_stage(stage)
                raise AssertionError(f"Changed IL2CPP input accepted: {key}")
            except ValueError:
                pass
        (stage / "BepInEx/config/BepInEx.cfg").write_text(IL2CPP_CONFIGURATION)
        (stage / LOCALIZATION_CONFIG).write_text("[Data]\nAutoUpdate = false\n[General]\nLanguage = en\nDialogueFontSize = 36\n")
        validate_stage(stage)
        (stage / LOCALIZATION_CONFIG).write_text("[Data]\nAutoUpdate = true\n")
        try:
            validate_stage(stage)
            raise AssertionError("Localization data auto-update accepted")
        except ValueError:
            pass
        (stage / LOCALIZATION_CONFIG).write_text("[Data]\nAutoUpdate = false\n")
        (stage / "BepInEx/plugins/AtelierCapture.dll").write_bytes(b"tampered")
        try:
            validate_stage(stage)
            raise AssertionError("Tampered plugin accepted")
        except ValueError:
            pass
        certificate["runtime_files"][0]["path"] = "../outside.dll"
        certificate_path.write_text(json.dumps(certificate))
        try:
            validate_stage(stage)
            raise AssertionError("Escaping runtime certificate accepted")
        except ValueError:
            pass
    print("Build/package boundary checks passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--workdir", type=Path, default=ROOT / "work/build")
    parser.add_argument("--output", type=Path, default=ROOT / "work/release")
    parser.add_argument("--version")
    parser.add_argument("--source-commit")
    parser.add_argument("--offline", action="store_true")
    parser.add_argument("--update-locks", action="store_true")
    parser.add_argument("--runtime-only", action="store_true")
    parser.add_argument("--self-check", action="store_true")
    args = parser.parse_args()
    if args.self_check:
        (ROOT / "work/checks").mkdir(parents=True, exist_ok=True)
        self_check()
        return
    if os.name != "nt":
        parser.error("This distribution is supported on Windows x64 only")
    sources = source_inventory(runtime_only=args.runtime_only)
    source_digest = digest_inventory(sources)
    version = ".".join(map(str, parse_version(args.version or (ROOT / "VERSION").read_text().strip())))
    if args.source_commit is None:
        args.source_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", args.source_commit):
        parser.error("--source-commit must identify a full delivery/base commit; source bytes are recorded separately")
    workdir = owned_output(args.workdir)
    workdir.mkdir(parents=True, exist_ok=True)
    toolchain = ROOT / "work/toolchain"
    sdk_archive = download("dotnet-sdk-10.0.402-win-x64.zip", toolchain, SDK_ARCHIVE, args.offline)
    dotnet = toolchain / "dotnet10/dotnet.exe"
    if not dotnet.exists():
        extract(sdk_archive, dotnet.parent)
    os.environ.update(DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_HOME=str(workdir / "cli"),
                      DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
                      DOTNET_GENERATE_ASPNET_CERTIFICATE="false", DOTNET_NOLOGO="1")
    actual_sdk = subprocess.check_output([str(dotnet), "--version"], cwd=ROOT, text=True).strip()
    if actual_sdk != SDK_VERSION:
        raise ValueError(f"SDK mismatch: {actual_sdk}")
    dependencies = workdir / "dependencies"
    for name, definition in DEPENDENCIES.items():
        download(name, dependencies, definition, args.offline)
    stage = owned_output(workdir / "stage")
    if stage.exists():
        shutil.rmtree(stage)
    stage.mkdir()
    extract(dependencies / "bepinex.zip", stage, install_only=True)
    with zipfile.ZipFile(dependencies / "bepinex.zip") as archive:
        for component in BOOTSTRAP_EXTENSIONS:
            name = f"Microsoft.Extensions.{component}.dll"
            # These are Bep application dependencies, separate from the replaced CLR/BCL.
            (stage / "BepInEx/core" / name).write_bytes(archive.read("dotnet/" + name))
    shutil.rmtree(stage / "dotnet")
    extract(dependencies / "dotnet-runtime-10.0.13.zip", stage / "dotnet", prefix=f"shared/Microsoft.NETCore.App/{RUNTIME_VERSION}/")
    extract(dependencies / "localization.zip", stage, install_only=True, plugins_only=True)
    localization_plugin = stage / LOCALIZATION_PATCH["path"]
    localization_plugin.write_bytes(patch_localization(localization_plugin.read_bytes()))
    unity_directory = stage / "BepInEx/unity-libs"
    unity_directory.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(dependencies / "2022.3.62.zip", unity_directory / "2022.3.62.zip")
    config_directory = stage / "BepInEx/config"
    config_directory.mkdir(exist_ok=True)
    (config_directory / "BepInEx.cfg").write_text(IL2CPP_CONFIGURATION + "\n[Logging.Disk]\nEnabled = true\nWriteUnityLog = true\nLogLevels = Fatal, Error, Warning, Message, Info\n", encoding="utf-8")
    shutil.copyfile(ROOT / ".config/AtelierResleriana.Plugin.Localization.cfg", stage / LOCALIZATION_CONFIG)
    compiled = workdir / "compiled"
    runtime = stage / "BepInEx/core"
    bepinex = ROOT / "Sources/BepInEx"
    interop = ROOT / "Sources/Il2CppInterop"
    build(dotnet, bepinex / "Runtimes/Unity/BepInEx.Unity.IL2CPP/BepInEx.Unity.IL2CPP.csproj", workdir,
          args.offline, args.update_locks, SolutionDir=str(bepinex) + "/", BuildDir=str(compiled / "BepInEx"),
          BuildArtifactsDirectory=str(compiled / "BepInEx") + "/", UnityReferenceDirectory=dependencies,
          Version="6.0.0-be.788-capture.2", SourceRevisionId=args.source_commit)
    for path in (compiled / "BepInEx/Unity.IL2CPP").glob("*.dll"):
        shutil.copyfile(path, runtime / path.name)
    for path in (compiled / "BepInEx/Unity.IL2CPP/runtimes/win-x64/native").glob("*.dll"):
        shutil.copyfile(path, runtime / path.name)
    build(dotnet, interop / "Il2CppInterop.HarmonySupport/Il2CppInterop.HarmonySupport.csproj", workdir,
          args.offline, args.update_locks, BuildArtifactsDirectory=str(compiled / "Il2CppInterop") + "/",
          Il2CppReferenceDirectory=dependencies, CopyLocalLockFileAssemblies="true",
          Version="1.5.3-capture.3", SourceRevisionId=args.source_commit)
    for component in ("Runtime", "Common", "HarmonySupport"):
        framework = "netstandard2.0" if component == "Common" else "net6.0"
        directory = compiled / f"Il2CppInterop/bin/Il2CppInterop.{component}/{framework}"
        path = directory / f"Il2CppInterop.{component}.dll"
        shutil.copyfile(path, runtime / path.name)
    terrafx = compiled / "Il2CppInterop/bin/Il2CppInterop.Runtime/net6.0/TerraFX.Interop.Windows.dll"
    shutil.copyfile(terrafx, runtime / terrafx.name)
    native_provenance = None
    if not args.runtime_only:
        native_output, native_provenance = build_native(workdir)
        shutil.copyfile(native_output, stage / "BepInEx/plugins/AtelierCapture.Native.dll")
        capture_output = compiled / "AtelierCapture"
        build(dotnet, ROOT / "Sources/AtelierCapture/AtelierCapture.csproj", workdir, args.offline, args.update_locks,
              RuntimeDirectory=runtime, ReferenceDirectory=dependencies, Version=version, SourceRevisionId=source_digest,
              BaseIntermediateOutputPath=str(workdir / "obj/AtelierCapture") + "/",
              OutputPath=str(capture_output) + "/", AppendTargetFrameworkToOutputPath="false")
        shutil.copyfile(capture_output / "AtelierCapture.dll", stage / "BepInEx/plugins/AtelierCapture.dll")
    shutil.copytree(ROOT / "Licenses", stage / "Licenses", dirs_exist_ok=True)
    with zipfile.ZipFile(dependencies / "dotnet-runtime-10.0.13.zip") as archive:
        for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
            if name in archive.namelist():
                (stage / "Licenses" / f"dotnet10-{name}").write_bytes(archive.read(name))
    control = ROOT / "Sources/AtelierCapture/Control/capture-control.ps1"
    if not args.runtime_only:
        shutil.copyfile(control, stage / "capture-control.ps1")
    shutil.copyfile(ROOT / "README.md", stage / "README.md")
    provenance = {"schema": 2, "source_commit": args.source_commit, "source_digest": source_digest,
                  "source_files": sources, "sdk": SDK_VERSION, "runtime": RUNTIME_VERSION,
                  "vendor_sources": [{"name": "BepInEx", "base": BEPINEX_COMMIT},
                                     {"name": "Il2CppInterop", "base": INTEROP_COMMIT, "scanner_backport": SCANNER_COMMIT}],
                  "dependencies": [{"name": name, "url": url, "hash_algorithm": algorithm, "hash": digest,
                                    "sha256": sha256(dependencies / name), "size": (dependencies / name).stat().st_size,
                                    **({"patches": [LOCALIZATION_PATCH]} if name == "localization.zip" else {})}
                                   for name, (url, algorithm, digest) in DEPENDENCIES.items()],
                  "packages": nuget_inventory(workdir / "packages"), "native_observer": native_provenance}
    (workdir / "dependencies.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    if source_inventory(runtime_only=args.runtime_only) != sources:
        raise ValueError("Source changed during the runtime/product build; rebuild this candidate")
    if args.runtime_only:
        print(f"Runtime prepared: {runtime}")
        return
    runtime_files = [{"path": path.relative_to(stage).as_posix(), "sha256": sha256(path), "size": path.stat().st_size}
                     for path in sorted(stage.rglob("*")) if path.is_file()
                     and path.relative_to(stage).parts[0] != "Licenses" and path.name != "README.md"
                     and path.relative_to(stage).as_posix() not in MUTABLE_CONFIG_FILES]
    certificate = {"schema": 1, "runtime_major": 10, "runtime_version": RUNTIME_VERSION,
                   "runtime_files": runtime_files, "supported_client": SUPPORTED_CLIENT, "source_digest": source_digest,
                   "configuration_contract": CONFIGURATION_CONTRACT}
    (stage / "BepInEx/plugins/capture-runtime.json").write_text(json.dumps(certificate, indent=2) + "\n", encoding="utf-8")
    subprocess.run([sys.executable, str(ROOT / "scripts/package.py"), "--stage", str(stage),
                    "--output", str(owned_output(args.output)), "--version", version], cwd=ROOT, check=True)
    if source_inventory() != sources:
        raise ValueError("Source changed during packaging; rebuild this candidate")


if __name__ == "__main__":
    main()
