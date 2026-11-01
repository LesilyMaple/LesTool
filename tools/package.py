#!/usr/bin/env python3
"""Build a complete, versioned Windows release from source manifests, never from old host bin."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import uuid
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[1]
SHARED = {"LesTool.Platform", "Serilog"}


def child_environment():
    env = os.environ.copy()
    profile = Path.home()
    drive = env.get("SystemDrive", "C:")
    for key, value in {
        "SystemRoot": Path(drive + "/Windows"), "SystemDrive": drive,
        "APPDATA": profile / "AppData/Roaming", "LOCALAPPDATA": profile / "AppData/Local",
        "ProgramData": Path(drive + "/ProgramData"), "ProgramFiles": Path(drive + "/Program Files"),
        "ProgramFiles(x86)": Path(drive + "/Program Files (x86)"),
    }.items():
        env.setdefault(key, str(value))
    env["DOTNET_CLI_UI_LANGUAGE"] = "en"
    env["MSBUILDDISABLENODEREUSE"] = "1"
    return env


def run(command, env, timeout=None):
    subprocess.run([str(arg) for arg in command], cwd=ROOT, env=env, check=True, timeout=timeout)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--configuration", default="Release", choices=["Release", "Debug"])
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/releases")
    parser.add_argument("--skip-tests", action="store_true")
    args = parser.parse_args()
    env = child_environment()
    dotnet = Path(env["ProgramFiles"]) / "dotnet/dotnet.exe"
    if not dotnet.is_file():
        raise RuntimeError(".NET 8 SDK was not found")
    version = subprocess.check_output([str(dotnet), "--version"], cwd=ROOT, env=env, text=True).strip()
    if not version.startswith("8."):
        raise RuntimeError("This repository requires .NET 8 SDK")
    manifests = []
    ids = set()
    for file in sorted((ROOT / "src/Plugins").glob("*/plugin.json")):
        manifest = json.loads(file.read_text(encoding="utf-8-sig"))
        if manifest["id"] in ids or manifest.get("apiContract") != 1:
            raise RuntimeError("Duplicate/unsupported plugin: " + manifest["id"])
        ids.add(manifest["id"])
        projects = list(file.parent.glob("*.csproj"))
        if len(projects) != 1:
            raise RuntimeError("Expected exactly one plugin project: " + str(file))
        manifests.append((file, projects[0], manifest))
    if not manifests:
        raise RuntimeError("No source plugin manifests")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    build_id = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:8]
    stage = output / (".staging-" + build_id)
    stage.mkdir()  # Own only this unique directory. Failures leave it for inspection.
    app = stage / "app"
    common = ["-c", args.configuration, "-nr:false", "-p:SkipPluginDeploy=true"]
    run([dotnet, "build", ROOT / "LesTool.sln", *common], env)
    if not args.skip_tests:
        run([sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_*.py"], env)
        run([dotnet, "run", "--project", ROOT / "tests/LesTool.Tests", *common], env)
    run([dotnet, "publish", ROOT / "src/LesTool/LesTool.csproj", *common,
         "-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true",
         "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", app], env)
    # Build-only project references must never populate release plugins implicitly.
    plugin_root = app / "plugins"
    plugin_root.mkdir(exist_ok=True)
    optional = {}
    for source, project, manifest in manifests:
        private_output = stage / ("plugin-" + manifest["id"])
        run([dotnet, "publish", project, *common, "-r", "win-x64", "--self-contained", "false", "-o", private_output], env)
        destination = plugin_root / manifest["id"]
        destination.mkdir()
        for item in private_output.iterdir():
            if item.stem in SHARED or item.name == "models":
                continue
            if item.is_dir(): shutil.copytree(item, destination / item.name)
            else: shutil.copy2(item, destination / item.name)
        assets = destination / "assets"
        if (private_output / "models").is_dir():
            shutil.copytree(private_output / "models", assets / "models", dirs_exist_ok=True)
        if (source.parent / "assets").is_dir():
            shutil.copytree(source.parent / "assets", assets, dirs_exist_ok=True)
        shutil.copy2(source, destination / "plugin.json")
        if not (destination / manifest["entryAssembly"]).is_file():
            raise RuntimeError("Missing plugin entry: " + manifest["id"])
        if not list(destination.glob("*.deps.json")):
            raise RuntimeError("Missing plugin dependency manifest: " + manifest["id"])
        if manifest["id"] == "screenshot":
            optional["ffmpeg"] = (assets / "ffmpeg.exe").is_file()
            optional["ocr_models"] = (assets / "models/v5").is_dir()
    actual = {p.name for p in plugin_root.iterdir() if p.is_dir()}
    if actual != ids:
        raise RuntimeError("Assembled plugin set differs from source")
    report = app / "plugin-diagnostics.json"
    run([app / "LesTool.exe", "--diagnose-plugins", plugin_root, report], env)
    diagnostics = json.loads(report.read_text(encoding="utf-8-sig"))
    if diagnostics["Errors"] or {p["Id"] for p in diagnostics["Plugins"]} != ids:
        raise RuntimeError("Plugin load diagnostics failed: " + str(diagnostics))
    startup_report = app / "plugin-startup.json"
    run([app / "LesTool.exe", "--verify-plugin-startup", startup_report], env, timeout=180)
    startup = json.loads(startup_report.read_text(encoding="utf-8-sig"))
    if startup["Errors"] or {p["Id"] for p in startup["Plugins"] if p["Status"] == "Loaded"} != ids:
        raise RuntimeError("Real plugin initialization failed: " + str(startup))
    try:
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, env=env, text=True, stderr=subprocess.DEVNULL).strip()
    except (subprocess.SubprocessError, OSError):
        revision = "unknown"
    source_hash = hashlib.sha256()
    for file in sorted((ROOT / "src").rglob("*")):
        if file.is_file() and not {"bin", "obj", ".mimosa", ".zcode", ".git", "__pycache__"}.intersection(file.relative_to(ROOT).parts):
            source_hash.update(file.relative_to(ROOT).as_posix().encode("utf-8"))
            source_hash.update(hashlib.sha256(file.read_bytes()).digest())
    files = [{"path": file.relative_to(app).as_posix(), "sha256": hashlib.sha256(file.read_bytes()).hexdigest()}
             for file in sorted(app.rglob("*")) if file.is_file()]
    (app / "package-manifest.json").write_text(json.dumps({"buildId": build_id, "sourceRevision": revision, "sourceContentSha256": source_hash.hexdigest(),
        "sdk": version, "rid": "win-x64", "apiContract": 1, "plugins": [m for _, _, m in manifests],
        "optionalAssets": optional, "files": files}, ensure_ascii=False, indent=2), encoding="utf-8")
    final = output / ("LesTool-" + build_id)
    app.rename(final)
    archive = shutil.make_archive(str(final), "zip", root_dir=final)
    if stage.parent.resolve() != output or stage.name != ".staging-" + build_id:
        raise RuntimeError("Unexpected staging directory; refusing cleanup")
    shutil.rmtree(stage)  # Only the unique staging directory created by this invocation.
    print("Verified release:", final)
    print("Archive:", archive)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (subprocess.SubprocessError, OSError, RuntimeError, ValueError, KeyError) as error:
        print("Packaging failed:", error, file=sys.stderr)
        raise SystemExit(1)
