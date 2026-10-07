#!/usr/bin/env python3
"""Smoke-test the built CLI and Finder wrapper without touching the user's documents or installation."""
import json
import pathlib
import subprocess
import sys
import tempfile

root = pathlib.Path(__file__).resolve().parents[2]
dotnet = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
cli = root / "MetadataDel.MacCli/bin/Release/net8.0/MetadataDel.dll"

with tempfile.TemporaryDirectory(prefix="metadatadel-smoke-") as folder:
    work = pathlib.Path(folder)

    def run_cli(*args):
        return subprocess.run([dotnet, str(cli), *args], cwd=work, capture_output=True, text=True)

    document = work / "unknown.txt"
    document.write_text("Author: Private")
    result = run_cli("--audit", "--log", document.name)
    assert result.returncode == 2 and "[CLEAN]" not in result.stdout, result
    assert document.read_text() == "Author: Private"
    assert not list(work.glob("*.MetadataDel-ошибка.txt"))
    result = run_cli("--backup=typo", document.name)
    assert result.returncode == 2 and "Неизвестный параметр" in result.stderr, result
    assert not list(work.glob("*.MetadataDel-ошибка.txt"))
    broken = work / "broken.docx"
    broken.write_bytes(b"broken document")
    result = run_cli(broken.name)
    assert result.returncode == 2, result
    assert broken.read_bytes() == b"broken document"
    assert (work / "broken.docx.MetadataDel-ошибка.txt").is_file()
    assert run_cli("--help").returncode == 0
    result = run_cli("--gui-clean", "--backup=on", "--", str(broken))
    payload = json.loads(result.stdout)
    assert result.returncode == 2 and not payload["success"] and payload["outputPath"] is None
    assert broken.read_bytes() == b"broken document"
    result = run_cli("--gui-clean", "--unknown", "--", str(broken))
    assert result.returncode == 2 and not json.loads(result.stdout)["success"]
    print("CLI: audit/invalid options/relative error report/original preservation/help PASS")

    script = (root / "scripts/mac/finder-action.sh").read_text()
    stub = work / "fake-cli"
    script = script.replace('BIN="$HOME/.local/bin/metadatadel"', f'BIN="{stub}"')
    script = script.replace('CONFIG="$HOME/Library/Application Support/MetadataDel/command-path"', f'CONFIG="{work}/missing-config"')
    script = script.replace('LOG_DIR="$HOME/Library/Logs/MetadataDel"', f'LOG_DIR="{work}/logs"')
    script = script.replace("/usr/bin/osascript", "/usr/bin/true")
    wrapper = work / "finder.sh"
    wrapper.write_text(script)
    for status in (0, 1, 2):
        stub.write_text(f"#!/bin/sh\nexit {status}\n")
        stub.chmod(0o700)
        result = subprocess.run(["bash", str(wrapper), str(broken)], capture_output=True)
        assert result.returncode == status, (status, result)
    print("Finder: CLI exit codes 0/1/2 PASS")

    captured = work / "finder-args.json"
    stub.write_text("#!/bin/sh\nprintf '%s\\n' \"$@\" > " + str(captured) + "\n")
    # Arguments are data: punctuation and spaces must remain a single file argument.
    special = str(work / "-Договор 'пример'.pdf")
    result = subprocess.run(["bash", str(wrapper), special], capture_output=True)
    assert result.returncode == 0, result
    assert captured.read_text().splitlines() == ["--log", "--backup=on", special]
    print("Finder: backup and literal argument preservation PASS")
