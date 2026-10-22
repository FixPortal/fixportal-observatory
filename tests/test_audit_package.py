"""Exercise the real archive boundary with invented publish bytes, without building .NET."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location("audit_package", ROOT / "scripts/package-audit-runner.py")
packager = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packager)


class PackagingTests(unittest.TestCase):
    def test_allowlist_metadata_checksums_and_reproducibility(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            published = base / "published"
            published.mkdir()
            for name in packager.PAYLOAD:
                (published / name).write_bytes(b"synthetic-only")
            (published / "AiObservatory.Audit.Cli.runtimeconfig.json").write_text(json.dumps({
                "runtimeOptions": {"framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}
            }))
            (published / "private-config.json").write_bytes(b"must never ship")
            for rid in packager.RIDS:
                with self.subTest(rid=rid):
                    archive = packager.package(published, base / "first" / rid, rid)
                    second = packager.package(published, base / "second" / rid, rid)
                    self.assertEqual(archive.read_bytes(), second.read_bytes())
                    with zipfile.ZipFile(archive) as bundle:
                        self.assertEqual(set(bundle.namelist()), {*packager.PAYLOAD, "LICENSE", "RUNNER.md", "release.json"})
                        metadata = json.loads(bundle.read("release.json"))
                        self.assertEqual(metadata["runtimeIdentifier"], rid)
                        self.assertEqual(metadata["sourceCommit"], subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip())
                        self.assertEqual(metadata["verifierVersion"], "1.0.0")
                        self.assertEqual(metadata["schemaVersion"], 1)
                        self.assertEqual(metadata["ruleVersion"], "1")
                        for item in metadata["files"]:
                            self.assertEqual(item["sha256"], hashlib.sha256(bundle.read(item["path"])).hexdigest())
                        self.assertEqual(archive.with_suffix(".release.json").read_bytes(), bundle.read("release.json"))
                    self.assertEqual(archive.with_name(archive.name + ".SHA256SUMS").read_text(), f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n")
                    with self.assertRaises(FileExistsError):
                        packager.package(published, base / "first" / rid, rid)

    def test_refuses_missing_payload_unknown_runtime_and_self_contained_config(self):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            with self.assertRaises(ValueError):
                packager.package(base, base / "out", "unknown")
            with self.assertRaises(ValueError):
                packager.package(base, base / "out", "linux-x64")
            for name in packager.PAYLOAD:
                (base / name).write_bytes(b"synthetic-only")
            (base / "AiObservatory.Audit.Cli.runtimeconfig.json").write_text('{"runtimeOptions":{"includedFrameworks":[]}}')
            with self.assertRaisesRegex(ValueError, "framework-dependent"):
                packager.package(base, base / "out", "linux-x64")


if __name__ == "__main__":
    unittest.main()
