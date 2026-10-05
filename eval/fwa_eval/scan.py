"""Scans a directory for secrets and identifiers before anything in it leaves the runner.

    python -m fwa_eval.scan <dir> [--literal-env NAME ...]
    python -m fwa_eval.scan --mask [--literal-env NAME ...]

The live evaluation (.github/workflows/live-eval.yml) runs it over its transcripts, dataset, scores
and report before it posts the report or uploads them (Review Focus 4). It looks for what the
runner's and the evaluation's redaction replace (Workshop.Agent's Redaction, run_eval.redact):
tokens, JWTs, URLs, ARM paths, Azure and Anthropic hosts, emails and GUIDs; and for keys in
connection strings and key-shaped values. Each --literal-env names an environment variable whose
value (a secret of the workflow's environment) must not appear, nor, for an endpoint, its host or
resource name: a resource name has no shape a pattern could catch. A connection string's values
count one by one, with the hosts and resource names in them.

--mask scans nothing: it prints `::add-mask::<value>` for each of those values, so GitHub masks
them in every later line of the job log. GitHub masks a secret's whole value only, never the host
or the resource name inside it, and an Azure error can print those. The workflow runs this first,
with the runner image's Python, so this module imports only the standard library.

A finding is printed as file:line: kind, never with the text it matched: the log is as public as
the artifact. Exit codes: 0 nothing found (or masked), 1 something found, 2 no such directory or a
bad command line.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from collections.abc import Mapping
from pathlib import Path
from urllib.parse import urlparse

# Each pattern with the kind it reports. Unlike run_eval's redaction there is no rule for long hex:
# the transcripts' instruction and settings hashes are SHA-256 (64 hex characters) by design, so
# only a 32-character hex value, the shape of an Azure AI services key, is flagged.
PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("bearer token", re.compile(r"\bbearer\s+[A-Za-z0-9\-._~+/]+=*", re.IGNORECASE)),
    ("JWT", re.compile(r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*")),
    ("URL", re.compile(r"\b(?:https?|wss?)://\S+", re.IGNORECASE)),
    ("ARM resource path", re.compile(r"/subscriptions/[^\s\"'<>/]+", re.IGNORECASE)),
    ("Azure or Anthropic host", re.compile(
        r"\b(?:[A-Za-z0-9-]+\.)+(?:azure\.com|azure\.net|windows\.net|microsoft\.com|microsoftonline\.com|onmicrosoft\.com"
        r"|anthropic\.com|azurewebsites\.net|azureml\.ms|azure-api\.net|azurefd\.net|azurecr\.io)\b",
        re.IGNORECASE)),
    ("email", re.compile(r"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+\b")),
    # Unanchored: a GUID joined to a letter or `_` (`id_<guid>`, `<guid>abc`) is still one.
    ("GUID", re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.IGNORECASE)),
    ("connection-string key", re.compile(
        r"\b(?:InstrumentationKey|AccountKey|SharedAccessKey|SharedAccessSignature|ApiKey)\s*=", re.IGNORECASE)),
    ("API key", re.compile(r"\bsk-[A-Za-z0-9_-]{20,}")),
    # Bounded by non-hex rather than \b: `key_<32 hex>` is found, a 64-hex SHA-256 is not.
    ("32-hex key", re.compile(r"(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", re.IGNORECASE)),
)

# A shorter value (a deployment name such as "gpt") would match ordinary text, and is no identifier.
MIN_LITERAL = 6


def findings_in(text: str) -> list[tuple[int, str]]:
    """(line, kind) for each pattern that matches a line of `text`, once per kind and line."""
    found = []
    for number, line in enumerate(text.splitlines(), start=1):
        found.extend((number, kind) for kind, pattern in PATTERNS if pattern.search(line))
    return found


def _parts(value: str) -> list[str]:
    """The value; each value of a `Key=value;...` connection string; each URL's host and the host's first label."""
    parts = [value]
    if "=" in value and "://" not in value.split("=", 1)[0]:
        parts += [p.split("=", 1)[1].strip() for p in value.split(";") if "=" in p]
    for part in list(parts):
        if "://" in part and urlparse(part).hostname:
            # The host as given and in lower case: masking is case-sensitive, and urlparse lowers it.
            given = part.split("://", 1)[1].split("/", 1)[0].split(":", 1)[0]
            for host in (given, given.lower()):
                parts += [host, host.split(".")[0]]
    return parts


def literals(names: list[str], environ: Mapping[str, str]) -> list[tuple[str, str]]:
    """(name, value) for each value to look for or mask, as given (see _parts); unset and short values are left out."""
    values = []
    for name in names:
        value = environ.get(name, "").strip()
        values += [(name, v) for v in dict.fromkeys(_parts(value)) if len(v) >= MIN_LITERAL]
    return values


def scan(directory: Path, secrets: list[tuple[str, str]]) -> list[str]:
    """`file:line: kind` for everything found under `directory`, in file order."""
    secrets = list(dict.fromkeys((name, value.lower()) for name, value in secrets))
    report = []
    for path in sorted(p for p in directory.rglob("*") if p.is_file()):
        # Undecodable bytes become replacement characters: a binary file is still read, never skipped.
        text = path.read_text(encoding="utf-8", errors="replace")
        relative = path.relative_to(directory).as_posix()
        found = findings_in(text)
        for number, line in enumerate(text.lower().splitlines(), start=1):
            found.extend((number, f"the value of {name}") for name, value in secrets if value in line)
        report += [f"{relative}:{number}: {kind}" for number, kind in sorted(dict.fromkeys(found))]
    return report


def main(argv: list[str] | None = None, environ: Mapping[str, str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m fwa_eval.scan", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dir", type=Path, nargs="?")
    parser.add_argument("--literal-env", action="append", default=[], metavar="NAME")
    parser.add_argument("--mask", action="store_true", help="print ::add-mask:: for each literal value instead of scanning")
    try:
        args = parser.parse_args(argv)
    except SystemExit:
        return 2

    environ = os.environ if environ is None else environ
    if args.mask:
        for value in dict.fromkeys(v for _, v in literals(args.literal_env, environ)):
            print(f"::add-mask::{value}")
        return 0

    if args.dir is None:
        print("A directory to scan is required (or --mask).", file=sys.stderr)
        return 2

    if not args.dir.is_dir():
        # Fail closed: a run that wrote nothing where it should have is not a clean one.
        print(f"There is no directory '{args.dir}' to scan.", file=sys.stderr)
        return 2

    files = sum(1 for p in args.dir.rglob("*") if p.is_file())
    report = scan(args.dir, literals(args.literal_env, environ))
    if report:
        for line in report:
            print(line, file=sys.stderr)
        print(f"{len(report)} finding(s) in {files} file(s): nothing here may be posted or uploaded.", file=sys.stderr)
        return 1

    print(f"Scanned {files} file(s): no secret or identifier found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
