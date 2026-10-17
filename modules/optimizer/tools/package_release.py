#!/usr/bin/env python3
"""Package source and validated preview artifacts without game binaries/worlds."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {".git", "bin", "obj", "__pycache__", ".pytest_cache", "artifacts", "local-smoke"}


def write_zip(path: Path, items: list[tuple[Path, str]]) -> None:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for source, name in sorted(items, key=lambda item: item[1]):
            if source.is_symlink():
                raise ValueError(f"Refusing symlink: {source}")
            archive.write(source, name)
    with zipfile.ZipFile(path) as archive:
        bad = archive.testzip()
        if bad:
            raise RuntimeError(f"Archive integrity check failed: {bad}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    destination = args.output_dir.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    stem = "NewfiesServerOptimizer-" + (ROOT / "VERSION").read_text().strip()
    mod = ROOT / "artifacts" / (stem + ".zip")
    manifest_path = ROOT / "artifacts/build-manifest.json"
    manifest = json.loads(manifest_path.read_text())
    if manifest.get("testsSkipped") is not False:
        raise RuntimeError("Cannot package a validated preview when required tests were skipped")
    if hashlib.sha256(mod.read_bytes()).hexdigest() != manifest["sha256"]:
        raise RuntimeError("Compiled mod differs from tested build manifest")
    validation = ROOT / "docs/validation-report.md"
    if not validation.is_file():
        raise RuntimeError("Write the actual validation report before packaging")
    sources = []
    for path in ROOT.rglob("*"):
        relative = path.relative_to(ROOT)
        if any(part in EXCLUDED for part in relative.parts) or not path.is_file():
            continue
        if path.suffix.lower() in {".dll", ".exe", ".vcdbs", ".pdb", ".zip", ".gz", ".so", ".dylib"}:
            raise RuntimeError(f"Unexpected binary in source selection: {relative}")
        sources.append((path, "newfies-server-optimizer/" + relative.as_posix()))
    source_zip = destination / (stem + "-source.zip")
    write_zip(source_zip, sources)
    bundle = destination / (stem + "-complete.zip")
    write_zip(bundle, [(source_zip, source_zip.name), (mod, mod.name),
                      (manifest_path, "build-manifest.json"), (validation, "validation-report.md"),
                      (ROOT / "artifacts/validation-build.log", "validation-build.log")])
    checksum_path = destination / (stem + "-SHA256SUMS.txt")
    checksum_path.write_text("".join(hashlib.sha256(p.read_bytes()).hexdigest() + "  " + p.name + "\n"
                                      for p in (source_zip, bundle, mod)))
    print(json.dumps({"source": str(source_zip), "complete": str(bundle), "checksums": str(checksum_path)}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
