#!/usr/bin/env python3
"""Build and package this preview using a separately installed official VS server."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def run(args: list[str], env: dict[str, str]) -> None:
    print("+", " ".join(args), flush=True)
    subprocess.run(args, cwd=ROOT, env=env, check=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, default=os.environ.get("VINTAGE_STORY"))
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--skip-tests", action="store_true", help="Build only; manifest records skipped validation")
    args = parser.parse_args()
    if args.game_dir is None:
        parser.error("--game-dir or VINTAGE_STORY must identify official Vintage Story 1.22.7")
    game = args.game_dir.resolve()
    for relative in ("VintagestoryAPI.dll", "VintagestoryLib.dll", "Lib/0Harmony.dll"):
        if not (game / relative).is_file():
            parser.error(f"Missing official dependency: {relative}")
    official_hashes = {
        "VintagestoryAPI.dll": "034283e7e9d98eae45ee63005576fd89badc3c995b531cc4c3fe46f3eb2d3296",
        "VintagestoryLib.dll": "e08f22b493b92feaf0aaeb79d22437ea0f7efc38aa7f72a04a47f98bc0e40df0",
        "Mods/VSSurvivalMod.dll": "d67b48a321403b2052b33c7d0caa99611f92350ffac73ea72672901dd87ccd7a",
        "Mods/VSEssentials.dll": "a28565c5c9181f8cc84b98a2b7457ab824b8ecb7763714448da1f7c245aecd6e",
    }
    for relative, expected in official_hashes.items():
        if hashlib.sha256((game / relative).read_bytes()).hexdigest() != expected:
            parser.error(f"{relative} does not match the inspected official 1.22.7 build")
    env = os.environ.copy()
    env["VINTAGE_STORY"] = str(game)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    project = ROOT / "src/NewfiesOptimizer/NewfiesOptimizer.csproj"
    if not project.exists():
        projects = sorted((ROOT / "src/NewfiesOptimizer").glob("*.csproj"))
        if len(projects) != 1:
            parser.error("Cannot identify profiler project")
        project = projects[0]
    run([args.dotnet, "build", str(project), "-c", "Release", "--nologo"], env)
    tests_run: list[str] = []
    if not args.skip_tests:
        for test in sorted((ROOT / "tests").glob("**/*.csproj")):
            if test.parent.name in {"SuiteIntegrationProbe", "StorageBenchmarkProbe", "ModpackBenchmarkProbe", "EntitySuiteProbe"}: continue
            run([args.dotnet, "run", "--project", str(test), "-c", "Release"], env)
            tests_run.append(str(test.relative_to(ROOT)))
        # Discovery is deliberately scoped: the independently runnable tools own their test suites.
        for directory in ("tools/stress-tester/tests", "tools/benchmark/tests", "tools/dashboard/tests", "tools/probe/tests"):
            folder = ROOT / directory
            if folder.exists() and list(folder.glob("test*.py")):
                run([sys.executable, "-m", "unittest", "discover", "-s", str(folder), "-v"], env)
                tests_run.append(directory)
    output = ROOT / "artifacts"
    output.mkdir(exist_ok=True)
    dlls = [p for p in (project.parent / "bin/Release/net10.0").glob("*.dll")
            if p.name.lower().startswith("newfies")]
    if len(dlls) != 1:
        raise RuntimeError(f"Expected exactly one own assembly, found {len(dlls)}")
    modinfo = project.parent / "modinfo.json"
    if not modinfo.exists():
        modinfo = project.parent / "bin/Release/net10.0/modinfo.json"
    modzip = output / ("NewfiesServerOptimizer-" + (ROOT / "VERSION").read_text().strip() + ".zip")
    with zipfile.ZipFile(modzip, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(dlls[0], dlls[0].name)
        archive.write(modinfo, "modinfo.json")
        for name in ("README.md", "docs/configuration.md", "docs/limitations.md", "docs/performance-0.9.0.md", "docs/performance-0.9.1.md", "docs/performance-0.10.0.md", "docs/performance-0.11.0.md", "docs/performance-0.12.0.md", "docs/performance-0.13.0.md", "docs/performance-0.14.0.md", "docs/performance-0.15.0.md", "docs/performance-0.16.0.md", "docs/performance-0.16.1.md", "docs/performance-0.17.0.md", "docs/performance-0.18.0.md", "docs/replacement-coverage.md"):
            path = ROOT / name
            if path.is_file():
                archive.write(path, "guides/" + path.name)
        if (ROOT / "config/optimizer.json").is_file():
            archive.write(ROOT / "config/optimizer.json", "guides/optimizer.example.json")
    dependencies = {}
    for relative in ("VintagestoryAPI.dll", "VintagestoryLib.dll", "Mods/VSEssentials.dll", "Mods/VSSurvivalMod.dll", "Lib/0Harmony.dll"):
        p = game / relative
        if p.exists():
            dependencies[relative] = hashlib.sha256(p.read_bytes()).hexdigest()
    manifest = {
        "artifact": modzip.name,
        "sha256": hashlib.sha256(modzip.read_bytes()).hexdigest(),
        "assemblySha256": hashlib.sha256(dlls[0].read_bytes()).hexdigest(),
        "gameVersion": "1.22.7",
        "runtime": "net10.0",
        "maturity": "engineering-preview",
        "testsSkipped": args.skip_tests,
        "testSuitesExecuted": tests_run,
        "buildDependencyHashes": dependencies,
        "proprietaryGameBinariesIncluded": False,
        "multiplayerPerformanceClaims": False,
    }
    (output / "build-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(modzip)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

