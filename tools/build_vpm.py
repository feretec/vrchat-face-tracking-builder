"""Build the VPM ZIP and index. Run with --write-index before committing a release."""

import argparse
import hashlib
import json
import re
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PACKAGE_ID = "com.feretec.vrchat-face-tracking-builder"
PACKAGE = ROOT / "Packages" / PACKAGE_ID
REPOSITORY = "https://github.com/feretec/vrchat-face-tracking-builder"
INDEX_URL = "https://raw.githubusercontent.com/feretec/vrchat-face-tracking-builder/main/index.json"


def build(output, write_index=False, check=False):
    manifest = json.loads((PACKAGE / "package.json").read_text(encoding="utf-8"))
    version = manifest["version"]
    if manifest["name"] != PACKAGE_ID or not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Invalid package name or stable release version")
    filename = f"{PACKAGE_ID}-{version}.zip"
    if manifest["url"] != f"{REPOSITORY}/releases/download/v{version}/{filename}":
        raise ValueError("package.json URL must match the release version")
    if not manifest.get("vpmDependencies", {}).get("com.vrchat.avatars"):
        raise ValueError("VRChat Avatars dependency is required")
    if (ROOT / "README.md").read_text(encoding="utf-8") != (PACKAGE / "README.md").read_text(encoding="utf-8"):
        raise ValueError("Root and package installation instructions differ")

    output.mkdir(parents=True, exist_ok=True)
    archive_path = output / filename
    allowed = {".cs", ".meta", ".uxml", ".uss", ".asmdef", ".json", ".md"}
    files = sorted(p for p in PACKAGE.rglob("*") if p.is_file())
    guids = set()
    # Store text uncompressed with fixed metadata: identical SHA-256 on Windows/Linux.
    # This allowlist prevents accidental redistribution of avatar assets or binaries.
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_STORED) as archive:
        for path in files:
            if path.is_symlink() or not path.resolve().is_relative_to(PACKAGE.resolve()):
                raise ValueError(f"External package file: {path}")
            if path.suffix not in allowed or path.stat().st_size > 2_000_000:
                raise ValueError(f"Unexpected package file: {path}")
            if path.suffix != ".meta" and not Path(str(path) + ".meta").is_file():
                raise ValueError(f"Missing Unity metadata: {path}")
            data = path.read_text(encoding="utf-8").replace("\r\n", "\n").encode("utf-8")
            if path.suffix == ".meta":
                match = re.search(rb"^guid: ([0-9a-f]{32})$", data, re.MULTILINE)
                if not match or match[1] in guids:
                    raise ValueError(f"Missing or duplicate Unity GUID: {path}")
                guids.add(match[1])
            entry = zipfile.ZipInfo(path.relative_to(PACKAGE).as_posix(), (1980, 1, 1, 0, 0, 0))
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)

    digest = hashlib.sha256(archive_path.read_bytes()).hexdigest()
    index_path = ROOT / "index.json"
    index = json.loads(index_path.read_text(encoding="utf-8")) if index_path.exists() else {
        "name": "VRChat Face Tracking Builder",
        "id": "com.feretec.vrchat-face-tracking-builder.repository",
        "url": INDEX_URL,
        "author": "feretec",
        "packages": {},
    }
    versions = index["packages"].setdefault(PACKAGE_ID, {"versions": {}})["versions"]
    entry = {**manifest, "zipSHA256": digest}
    if check and versions.get(version) != entry:
        raise ValueError("index.json is stale; run python tools/build_vpm.py --write-index")
    versions[version] = entry
    content = json.dumps(index, ensure_ascii=False, indent=2) + "\n"
    (output / "index.json").write_text(content, encoding="utf-8", newline="\n")
    if write_index:
        index_path.write_text(content, encoding="utf-8", newline="\n")
    print(json.dumps({"archive": str(archive_path), "files": len(files), "sha256": digest}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "dist" / "vpm")
    parser.add_argument("--write-index", action="store_true")
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    build(args.output, args.write_index, args.check)
