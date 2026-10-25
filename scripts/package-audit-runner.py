"""Package an explicitly published verifier; stdlib only, no bundle inputs or downloads."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parent.parent
PAYLOAD = (
    "AiObservatory.Audit.Cli.dll",
    "AiObservatory.Audit.dll",
    "AiObservatory.Audit.Cli.deps.json",
    "AiObservatory.Audit.Cli.runtimeconfig.json",
)
RIDS = ("win-x64", "linux-x64", "osx-arm64")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def package(published, output, rid):
    if rid not in RIDS:
        raise ValueError("unsupported runtime")
    source = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    identity = (ROOT / "src/AiObservatory.Audit/AuditVerifierIdentity.cs").read_text(encoding="utf-8")
    schema = int(re.search(r"SchemaVersion = (\d+);", identity)[1])
    verifier = re.search(r'VerifierVersion = "([0-9.]+)";', identity)[1]
    rules = re.search(r'RuleVersion = "([0-9.]+)";', identity)[1]
    # Exclude debug symbols, source files and arbitrary publish-directory additions.
    files = {}
    for name in PAYLOAD:
        file = published / name
        if file.is_symlink() or not file.is_file():
            raise ValueError("missing or linked payload")
        files[name] = file.read_bytes()
    runtime = json.loads(files["AiObservatory.Audit.Cli.runtimeconfig.json"])["runtimeOptions"]
    if runtime.get("framework", {}).get("name") != "Microsoft.NETCore.App" or not runtime["framework"]["version"].startswith("10."):
        raise ValueError("expected framework-dependent .NET 10 runtime")
    files["LICENSE"] = (ROOT / "LICENSE").read_bytes()
    files["NOTICE"] = (ROOT / "NOTICE").read_bytes()
    files["RUNNER.md"] = (ROOT / "docs/self-audit.md").read_bytes()
    metadata = {
        "sourceCommit": source, "runtimeIdentifier": rid,
        "schemaVersion": schema, "verifierVersion": verifier, "ruleVersion": rules,
        "runtimeRequirement": ".NET 10 Microsoft.NETCore.App (framework-dependent)",
        "files": [{"path": name, "sha256": sha256(data)} for name, data in sorted(files.items())],
    }
    files["release.json"] = (json.dumps(metadata, sort_keys=True, indent=2) + "\n").encode()
    output.mkdir(parents=True, exist_ok=True)
    archive = output / f"audit-verifier-{verifier}-{rid}.zip"
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED) as bundle:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            bundle.writestr(entry, data)
    # Per-RID sidecars avoid shared append races; each manifest hashes the actual archive.
    (output / f"{archive.name}.SHA256SUMS").write_text(
        f"{sha256(archive.read_bytes())}  {archive.name}\n", encoding="utf-8"
    )
    (output / f"{archive.stem}.release.json").write_bytes(files["release.json"])
    return archive


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--rid", required=True, choices=RIDS)
    args = parser.parse_args()
    print(package(args.publish_dir, args.output, args.rid).name)
