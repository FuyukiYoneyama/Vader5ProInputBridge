"""Audit tracked files and reachable Git history without printing matched values."""
import argparse
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {"private", "history", "logs", "apps", "artifacts", "dist", "bin", "obj", ".appdata", ".dotnet-cli", ".nuget-packages", ".vs"}
PRIVATE_FILES = {"config/application.json", "config/measurement-id.txt"}
PRIVATE_SUFFIXES = {".log", ".jsonl", ".docx", ".dmp", ".bundle", ".exe", ".dll", ".pdb", ".pfx", ".p12", ".pem", ".key"}
ASSET_SUFFIXES = {".png", ".ico"}
PATTERNS = {
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "github-token": re.compile(r"\b(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}\b"),
    "api-token": re.compile(r"\bsk-(?:proj-)?[A-Za-z0-9_-]{24,}\b"),
    "aws-access-key": re.compile(r"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "personal-path": re.compile(r"[A-Za-z]:[/\\](?:Users|Documents|Downloads)[/\\]", re.I),
    "machine-name": re.compile(r"\bDESKTOP-[A-Z0-9]{5,}\b", re.I),
    "user-sid": re.compile(r"\bS-1-5-21-\d+-\d+-\d+-\d+\b"),
    "device-instance": re.compile(r"HID[#\\]VID_[0-9A-F]{4}.*?#[A-Za-z0-9&]{8,}", re.I),
}


def git(*args, data=None):
    return subprocess.run(["git", "-C", str(ROOT), *args], input=data, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, check=True).stdout


def check_path(name):
    path = PurePosixPath(name)
    return (any(part.lower() in EXCLUDED for part in path.parts)
            or name.lower() in PRIVATE_FILES or path.suffix.lower() in PRIVATE_SUFFIXES
            or path.name.lower() == ".env" or path.name.lower().startswith(".env."))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--private-values-file", type=Path, help="Local JSON array of values to check; keep this file outside Git.")
    args = parser.parse_args()
    local_values = json.loads(args.private_values_file.read_text(encoding="utf-8-sig")) if args.private_values_file else []
    if not isinstance(local_values, list) or any(not isinstance(v, str) or len(v) < 4 for v in local_values):
        raise ValueError("Use a JSON array of strings with at least four characters.")
    issues = set()
    inspected = 0

    def inspect(name, content, location):
        nonlocal inspected
        inspected += 1
        if check_path(name):
            issues.add((location, name, "private-file"))
        if PurePosixPath(name).suffix.lower() in ASSET_SUFFIXES:
            return
        try:
            text = content.decode("utf-8-sig")
        except UnicodeDecodeError:
            issues.add((location, name, "unexpected-binary"))
            return
        for rule, pattern in PATTERNS.items():
            if pattern.search(text):
                issues.add((location, name, rule))
        folded = text.casefold()
        if any(value.casefold() in folded for value in local_values):
            issues.add((location, name, "local-private-value"))

    paths = git("ls-files", "-z").decode().split("\0")
    for name in filter(None, paths):
        inspect(name, (ROOT / name).read_bytes(), "working-tree")

    objects = git("rev-list", "--objects", "--all").decode().splitlines()
    named = {}
    for row in objects:
        oid, _, name = row.partition(" ")
        if name:
            named.setdefault(oid, set()).add(name)
    if named:
        descriptions = git("cat-file", "--batch-check", data=("\n".join(named) + "\n").encode()).decode().splitlines()
        for row in descriptions:
            oid, kind, _ = row.split()
            if kind == "blob":
                content = git("cat-file", "blob", oid)
                for name in named[oid]:
                    inspect(name, content, "history")

    print(json.dumps({"tracked_files": len(list(filter(None, paths))), "inspected_file_versions": inspected,
                      "findings": [{"location": loc, "file": path, "rule": rule} for loc, path, rule in sorted(issues)]}, ensure_ascii=False, indent=2))
    return 1 if issues else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(type(exc).__name__ + ": publication audit could not complete.", file=sys.stderr)
        sys.exit(2)
