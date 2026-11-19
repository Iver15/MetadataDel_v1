#!/usr/bin/env python3
"""Smoke-test the built CLI and Finder wrapper without touching the user's documents or installation."""
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import zipfile

root = pathlib.Path(__file__).resolve().parents[2]
dotnet = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
cli = root / "MetadataDel.MacCli/bin/Release/net10.0/MetadataDel.dll"


def minimal_docx(path):
    with zipfile.ZipFile(path, "w") as package:
        package.writestr("[Content_Types].xml",
            '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
            '<Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/></Types>')
        package.writestr("_rels/.rels",
            '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>'
            '<Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/></Relationships>')
        package.writestr("word/document.xml",
            '<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
            '<w:body><w:p><w:r><w:t>Содержание</w:t></w:r></w:p></w:body></w:document>')
        package.writestr("docProps/core.xml",
            '<?xml version="1.0" encoding="UTF-8"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" '
            'xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>Private author</dc:creator></cp:coreProperties>')


with tempfile.TemporaryDirectory(prefix="metadatadel-smoke-") as folder:
    work = pathlib.Path(folder)
    # A private HOME: backups, logs and error reports of the CLI must land here, never in the real profile.
    home = work / "home"
    home.mkdir()
    env = dict(os.environ, HOME=str(home))
    logs = home / "Library/Logs/MetadataDel"
    backups = home / "Library/Application Support/MetadataDel/Backups"
    docs = work / "docs"
    docs.mkdir()

    def run_cli(*args):
        return subprocess.run([dotnet, str(cli), *args], cwd=docs, capture_output=True, text=True, env=env)

    def reports():
        return sorted(logs.glob("*.MetadataDel-ошибка.txt")) if logs.exists() else []

    document = docs / "unknown.txt"
    document.write_text("Author: Private")
    result = run_cli("--audit", "--log", document.name)
    assert result.returncode == 2 and "[CLEAN]" not in result.stdout, result
    assert document.read_text() == "Author: Private"
    assert not reports()
    result = run_cli("--backup=typo", document.name)
    assert result.returncode == 2 and "Неизвестный параметр" in result.stderr, result
    assert not reports()
    broken = docs / "broken.docx"
    broken.write_bytes(b"broken document")
    result = run_cli(broken.name)
    assert result.returncode == 2, result
    assert broken.read_bytes() == b"broken document"
    # The explanation goes to the private logs folder and its path is printed; nothing is written next to the file.
    assert len(reports()) == 1 and str(reports()[0]) in result.stderr, (result, reports())
    assert sorted(p.name for p in docs.iterdir()) == ["broken.docx", "unknown.txt"], list(docs.iterdir())
    assert run_cli("--help").returncode == 0
    result = run_cli("--gui-clean", "--backup=on", "--", str(broken))
    payload = json.loads(result.stdout)
    assert result.returncode == 2 and not payload["success"] and payload["outputPath"] is None
    assert payload["schemaVersion"] == 2 and payload["incomplete"] is False
    assert broken.read_bytes() == b"broken document"
    result = run_cli("--gui-clean", "--unknown", "--", str(broken))
    assert result.returncode == 2 and not json.loads(result.stdout)["success"]
    print("CLI: audit/invalid options/private error report/original preservation/help PASS")

    sample = docs / "Отчёт.docx"
    minimal_docx(sample)
    original = sample.read_bytes()
    if sys.platform == "darwin":
        subprocess.run(["/usr/bin/xattr", "-w", "com.apple.metadata:kMDItemWhereFroms", "https://mail.example/from-boss", str(sample)], check=True)
    result = run_cli("--backup=on", str(sample))
    assert result.returncode == 0, result
    saved = list(backups.rglob("Отчёт.docx"))
    assert len(saved) == 1 and saved[0].read_bytes() == original, saved
    assert "[BACKUP]" in result.stdout and str(saved[0]) in result.stdout, result
    assert b"Private author" not in sample.read_bytes()
    assert sorted(p.name for p in docs.iterdir()) == ["broken.docx", "unknown.txt", "Отчёт.docx"], list(docs.iterdir())
    if sys.platform == "darwin":
        names = subprocess.run(["/usr/bin/xattr", str(sample)], capture_output=True, text=True).stdout.split()
        assert "com.apple.metadata:kMDItemWhereFroms" not in names, names
    print("CLI: private backup folder, no backup next to document, extended attributes dropped PASS")

    script = (root / "scripts/mac/finder-action.sh").read_text()
    stub = work / "fake-cli"
    notifications = work / "notifications.txt"
    osascript = work / "fake-osascript"
    osascript.write_text(f"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{notifications}'\n")
    osascript.chmod(0o700)
    script = script.replace('BIN="$HOME/.local/bin/metadatadel"', f'BIN="{stub}"')
    script = script.replace('CONFIG="$HOME/Library/Application Support/MetadataDel/command-path"', f'CONFIG="{work}/missing-config"')
    script = script.replace('LOG_DIR="$HOME/Library/Logs/MetadataDel"', f'LOG_DIR="{work}/logs"')
    script = script.replace("/usr/bin/osascript", str(osascript))
    wrapper = work / "finder.sh"
    wrapper.write_text(script)
    expected = {0: "Метаданные удалены", 1: "Очищено не полностью", 2: "завершилась с ошибкой"}
    for status in (0, 1, 2):
        stub.write_text(f"#!/bin/sh\nexit {status}\n")
        stub.chmod(0o700)
        notifications.write_text("")
        result = subprocess.run(["bash", str(wrapper), str(broken)], capture_output=True)
        assert result.returncode == status, (status, result)
        message = notifications.read_text()
        assert expected[status] in message, (status, message)
        if status:
            assert "Метаданные удалены" not in message, message
    print("Finder: CLI exit codes 0/1/2 and distinct partial notification PASS")

    captured = work / "finder-args.json"
    stub.write_text("#!/bin/sh\nprintf '%s\\n' \"$@\" > " + str(captured) + "\n")
    # Arguments are data: punctuation and spaces must remain a single file argument, and a leading "-" is not an option.
    special = "-Договор 'пример' \\ \"кавычки\".pdf"
    notifications.write_text("")
    result = subprocess.run(["bash", str(wrapper), special], capture_output=True, cwd=work)
    assert result.returncode == 0, result
    assert captured.read_text().splitlines() == ["--log", "--backup=on", "--", special]
    print("Finder: backup, option terminator and literal argument preservation PASS")
