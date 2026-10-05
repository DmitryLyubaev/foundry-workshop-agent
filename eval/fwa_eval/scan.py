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
count one by one, with the hosts and resource names in them, except its region labels (eastus2,
eastus2-0) and generic suffixes, which identify nothing.

--mask scans nothing: it prints `::add-mask::<value>` for each of those values, so GitHub masks
them in every later line of the job log. A value with a line break is masked line by line: printed
whole, its break would end the command, and the rest of the value would be printed in the clear. GitHub masks a secret's whole value only, never the host
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


# An Azure region as a host label or setting: eastus2, westeurope-5, australiaeast, uksouth. Every
# region's name holds a compass word. Applied only to what a connection string gives: there, the
# region names the shared regional ingestion host, not the owner's resource, and every report names
# it (the price note's "Global Standard, eastus2").
_REGION_LABEL = re.compile(r"[a-z]*(?:east|west|north|south|central)[a-z]*\d*(?:-\d+)?", re.IGNORECASE)

# A connection-string value that is a bare domain, such as EndpointSuffix=applicationinsights.azure.com:
# a generic Azure suffix, the same for everyone. A URL's host is not one of these and is kept.
_BARE_DOMAIN = re.compile(r"[a-z0-9-]+(?:\.[a-z0-9-]+)+", re.IGNORECASE)


def _parts(value: str) -> list[str]:
    """The value; each value of a `Key=value;...` connection string; each URL's host and the host's first label.

    From a connection string, a region label (as a value or a host's first label) and a bare-domain
    value are left out: they identify nothing, and would make every report naming the region a finding.
    The instrumentation key, the application id and the ingestion and live hosts are kept.
    """
    parts = [value]
    connection_string = "=" in value and "://" not in value.split("=", 1)[0]
    if connection_string:
        settings = [p.split("=", 1)[1].strip() for p in value.split(";") if "=" in p]
        parts += [s for s in settings if not (_REGION_LABEL.fullmatch(s) or _BARE_DOMAIN.fullmatch(s))]
    for part in list(parts):
        if "://" in part and urlparse(part).hostname:
            # The host as given and in lower case: masking is case-sensitive, and urlparse lowers it.
            given = part.split("://", 1)[1].split("/", 1)[0].split(":", 1)[0]
            for host in (given, given.lower()):
                label = host.split(".")[0]
                parts += [host] if connection_string and _REGION_LABEL.fullmatch(label) else [host, label]
    return parts


def _lines(value: str) -> list[str]:
    """The value's lines, each masked and looked for on its own: one ::add-mask:: command is one line,
    and the scan reads line by line. A line shorter than MIN_LITERAL is left out, as a short value is."""
    return [line for line in (part.strip() for part in re.split(r"\r\n|\r|\n", value)) if len(line) >= MIN_LITERAL]


def literals(names: list[str], environ: Mapping[str, str]) -> list[tuple[str, str]]:
    """(name, value) for each value to look for or mask, as given (see _parts); unset and short values are left out."""
    values = []
    for name in names:
        value = environ.get(name, "").strip()
        values += [(name, v) for v in dict.fromkeys(_parts(value)) if len(v) >= MIN_LITERAL]
    return values


def scan(directory: Path, secrets: list[tuple[str, str]]) -> list[str]:
    """`file:line: kind` for everything found under `directory`, in file order."""
    secrets = list(dict.fromkeys((name, line.lower()) for name, value in secrets for line in _lines(value)))
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
        for value in dict.fromkeys(line for _, v in literals(args.literal_env, environ) for line in _lines(v)):
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
