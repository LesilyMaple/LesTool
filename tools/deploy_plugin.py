#!/usr/bin/env python3
"""Replace one generated development plugin directory without mixing old build layouts."""
import argparse
import os
from pathlib import Path
import re
import shutil
import uuid

ROOT = Path(__file__).resolve().parents[1]


def owned_child(target, parent):
    target, parent = target.resolve(), parent.resolve()
    if target.parent != parent or target == parent:
        raise ValueError("Refusing operation outside the generated plugin directory: " + str(target))
    return target


def deploy(source, host_output, plugin_id, project, allowed_output=None):
    source, host_output, project = Path(source).resolve(), Path(host_output).resolve(), Path(project).resolve()
    allowed = Path(allowed_output or ROOT / "src/LesTool/bin").resolve()
    if host_output == allowed or not host_output.is_relative_to(allowed):
        raise ValueError("HostOutput must be inside the workspace's generated host bin directory")
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]*", plugin_id) or ".." in plugin_id:
        raise ValueError("Invalid plugin id")
    manifest = project / "plugin.json"
    if not source.is_dir() or not manifest.is_file():
        raise ValueError("Missing plugin build output/manifest")
    parent = host_output / "plugins"
    if not parent.resolve().is_relative_to(host_output):
        raise ValueError("Plugin directory redirects outside the generated host output")
    parent.mkdir(parents=True, exist_ok=True)
    destination = owned_child(parent / plugin_id, parent)
    stage = owned_child(parent / (".deploy-" + plugin_id + "-" + uuid.uuid4().hex), parent)
    previous = owned_child(parent / (".previous-" + plugin_id + "-" + uuid.uuid4().hex), parent)
    stage.mkdir()
    try:
        for item in source.iterdir():
            if item.name.startswith(("LesTool.Platform.", "Serilog.")):
                continue
            if item.is_file():
                shutil.copy2(item, stage / item.name)
            elif item.name in {"runtimes", "assets"} or any(item.glob("*.resources.dll")):
                # RID output/publish directories (e.g. win-x64) are never recursively copied.
                shutil.copytree(item, stage / item.name)
        if (source / "models").is_dir():
            shutil.copytree(source / "models", stage / "assets/models", dirs_exist_ok=True)
        if (project / "assets").is_dir():
            shutil.copytree(project / "assets", stage / "assets", dirs_exist_ok=True)
        shutil.copy2(manifest, stage / "plugin.json")
        if destination.exists():
            os.replace(owned_child(destination, parent), owned_child(previous, parent))
        try:
            os.replace(owned_child(stage, parent), owned_child(destination, parent))
        except OSError:
            if previous.exists(): os.replace(owned_child(previous, parent), owned_child(destination, parent))
            raise
        if previous.exists(): shutil.rmtree(owned_child(previous, parent))
    finally:
        if stage.exists(): shutil.rmtree(owned_child(stage, parent))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--host-output", required=True)
    parser.add_argument("--id", required=True)
    parser.add_argument("--project", required=True)
    args = parser.parse_args()
    deploy(args.source, args.host_output, args.id, args.project)
