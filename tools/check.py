#!/usr/bin/env python3
"""Run the game-independent checks once, without launching or modifying a game."""
import ast
from pathlib import Path
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def main():
    dotnet = shutil.which("dotnet")
    if not dotnet and Path("/usr/local/share/dotnet/dotnet").is_file():
        dotnet = "/usr/local/share/dotnet/dotnet"
    if not dotnet:
        raise SystemExit("Install the .NET 9 SDK and add dotnet to PATH.")
    for script in (ROOT / "tools").glob("*.py"):
        ast.parse(script.read_text(encoding="utf-8"), filename=str(script))
    for project in ("tests/StorageTests.csproj", "tests/ConfigFingerprintCheck/Check.csproj", "tests/HistoryTrailCheck/Check.csproj"):
        subprocess.run([dotnet, "run", "--project", str(ROOT / project), "--configuration", "Release"], cwd=ROOT, check=True)
    print("ALL_INDEPENDENT_CHECKS_PASSED")

if __name__ == "__main__":
    main()
