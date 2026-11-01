import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("deployment", Path(__file__).resolve().parents[1] / "tools/deploy_plugin.py")
DEPLOY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DEPLOY)


class DeploymentTests(unittest.TestCase):
    def test_incremental_output_replaces_old_files_and_ignores_nested_rid(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, project, host = root / "source", root / "project", root / "bin/Release"
            source.mkdir(); project.mkdir()
            (project / "plugin.json").write_text(json.dumps({"id": "fixture"}))
            (source / "fixture.dll").write_bytes(b"new")
            (source / "win-x64").mkdir(); (source / "win-x64/fixture.dll").write_bytes(b"old")
            (source / "zh-Hans").mkdir(); (source / "zh-Hans/fixture.resources.dll").write_bytes(b"resource")
            (source / "Serilog.dll").write_bytes(b"shared")
            destination = host / "plugins/fixture"
            destination.mkdir(parents=True); (destination / "stale.dll").write_bytes(b"stale")
            DEPLOY.deploy(source, host, "fixture", project, root / "bin")
            self.assertEqual((destination / "fixture.dll").read_bytes(), b"new")
            self.assertTrue((destination / "zh-Hans/fixture.resources.dll").exists())
            self.assertFalse((destination / "win-x64").exists())
            self.assertFalse((destination / "stale.dll").exists())
            self.assertFalse((destination / "Serilog.dll").exists())
            DEPLOY.deploy(source, host, "fixture", project, root / "bin")
            self.assertEqual([p.name for p in (host / "plugins").iterdir()], ["fixture"])

    def test_target_outside_generated_output_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(ValueError):
                DEPLOY.deploy(root, root / "user-data", "fixture", root, root / "bin")
            self.assertFalse((root / "user-data").exists())

    def test_missing_manifest_leaves_previous_plugin_intact(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            old = root / "bin/Release/plugins/fixture/old.dll"
            old.parent.mkdir(parents=True); old.write_bytes(b"old")
            with self.assertRaises(ValueError):
                DEPLOY.deploy(root, root / "bin/Release", "fixture", root, root / "bin")
            self.assertEqual(old.read_bytes(), b"old")


if __name__ == "__main__": unittest.main()
