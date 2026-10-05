"""The secret scan the live evaluation runs over its outputs before it posts or uploads them.

Every value here is made up: placeholder GUIDs, example hosts and fake tokens.
"""

from __future__ import annotations

import json

import pytest

from conftest import RECORDED
from fwa_eval import scan


@pytest.mark.parametrize(
    "text, kind",
    [
        ("Authorization: Bearer abc.def-ghi", "bearer token"),
        ("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl", "JWT"),
        ("see https://example.org/path", "URL"),
        ("/subscriptions/x/resourceGroups/rg", "ARM resource path"),
        ("fwa-a1b2c3.services.ai.azure.com", "Azure or Anthropic host"),
        ("stexample.blob.core.windows.net", "Azure or Anthropic host"),
        ("api.anthropic.com", "Azure or Anthropic host"),
        ("someone@example.com", "email"),
        ("00000000-0000-0000-0000-000000000000", "GUID"),
        ("InstrumentationKey=abc;IngestionEndpoint=x", "connection-string key"),
        ("AccountKey=abc", "connection-string key"),
        ("sk-ant-0123456789abcdefghijkl", "API key"),
        ("key 0123456789abcdef0123456789abcdef", "32-hex key"),
    ],
)
def test_it_finds(text, kind):
    assert kind in [k for _, k in scan.findings_in(text)]


@pytest.mark.parametrize(
    "text",
    [
        # A transcript's instruction and settings hashes are SHA-256: 64 hex characters, not a key.
        '"instructionsSha256": "' + "a1" * 32 + '"',
        '"deployment": "gpt-5.6-luna"',
        "The job for the Lee family is booked for 10:30.",
        "Costs are the recorded tokens at the prices read on 2026-10-04",
    ],
)
def test_it_passes(text):
    assert scan.findings_in(text) == []


def test_the_recorded_transcripts_are_clean(capsys):
    assert scan.main([str(RECORDED)], environ={}) == 0
    assert "no secret or identifier" in capsys.readouterr().out


def test_a_finding_fails_naming_the_file_line_and_kind_but_never_the_text(tmp_path, capsys):
    secret = "11111111-2222-3333-4444-555555555555"
    (tmp_path / "s01.gpt.p1.json").write_text(json.dumps({"a": 1}) + "\n" + json.dumps({"error": f"principal {secret}"}) + "\n", encoding="utf-8")

    assert scan.main([str(tmp_path)], environ={}) == 1

    out = capsys.readouterr()
    text = out.out + out.err
    assert "s01.gpt.p1.json:2: GUID" in text
    assert secret not in text


def test_a_secret_value_from_the_environment_is_found_by_value_host_and_resource_name(tmp_path, capsys):
    environ = {"FWA_PROJECT_ENDPOINT": "https://fwa-z9y8x7.services.ai.azure.com/api/projects/example"}
    (tmp_path / "whole.txt").write_text("https://fwa-z9y8x7.services.ai.azure.com/api/projects/example", encoding="utf-8")
    (tmp_path / "name.txt").write_text("the resource FWA-Z9Y8X7 answered", encoding="utf-8")

    assert scan.main([str(tmp_path), "--literal-env", "FWA_PROJECT_ENDPOINT"], environ=environ) == 1

    out = capsys.readouterr()
    text = out.out + out.err
    assert "name.txt:1: the value of FWA_PROJECT_ENDPOINT" in text
    assert "whole.txt:1: the value of FWA_PROJECT_ENDPOINT" in text
    assert "z9y8x7" not in text.lower()


def test_a_short_or_unset_literal_is_ignored(tmp_path):
    (tmp_path / "t.json").write_text('"deployment": "gpt"', encoding="utf-8")

    assert scan.main([str(tmp_path), "--literal-env", "SHORT", "--literal-env", "UNSET"], environ={"SHORT": "gpt"}) == 0


def test_files_in_subdirectories_are_scanned(tmp_path):
    (tmp_path / "nested").mkdir()
    (tmp_path / "nested" / "report.md").write_text("contact someone@example.com", encoding="utf-8")

    assert scan.main([str(tmp_path)], environ={}) == 1


def test_a_missing_directory_fails_closed(tmp_path, capsys):
    assert scan.main([str(tmp_path / "nowhere")], environ={}) == 2
    assert "no directory" in capsys.readouterr().err.lower()
