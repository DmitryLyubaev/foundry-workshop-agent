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
        # An identifier joined to a letter or an underscore is still found.
        ("id_00000000-0000-0000-0000-000000000000", "GUID"),
        ("x00000000-0000-0000-0000-000000000000", "GUID"),
        ("a00000000-0000-0000-0000-000000000000", "GUID"),
        ("00000000-0000-0000-0000-000000000000abc", "GUID"),
        ("key_0123456789abcdef0123456789abcdef", "32-hex key"),
        ("x0123456789abcdef0123456789abcdefz", "32-hex key"),
        # Tenant domains and more Azure service hosts.
        ("tenant example.onmicrosoft.com", "Azure or Anthropic host"),
        ("ws-example.eastus2.api.azureml.ms", "Azure or Anthropic host"),
        ("apim-example.azure-api.net", "Azure or Anthropic host"),
        ("example.azurefd.net", "Azure or Anthropic host"),
        ("crexample.azurecr.io", "Azure or Anthropic host"),
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


CONNECTION_STRING = (
    "InstrumentationKey=00000000-0000-0000-0000-000000000000;"
    "IngestionEndpoint=https://eastus2-0.in.applicationinsights.azure.com/;"
    "LiveEndpoint=https://eastus2.livediagnostics.monitor.azure.com/;"
    "ApplicationId=11111111-1111-1111-1111-111111111111"
)


def test_a_connection_strings_parts_are_literals():
    values = {v for _, v in scan.literals(["FWA_APPINSIGHTS_CONNECTION_STRING"], {"FWA_APPINSIGHTS_CONNECTION_STRING": CONNECTION_STRING})}

    assert "00000000-0000-0000-0000-000000000000" in values
    assert "11111111-1111-1111-1111-111111111111" in values
    assert "eastus2-0.in.applicationinsights.azure.com" in values
    assert "eastus2.livediagnostics.monitor.azure.com" in values


def test_mask_prints_an_add_mask_command_for_each_hosts_and_resource_name(capsys):
    environ = {
        "FWA_PROJECT_ENDPOINT": "https://FWA-z9y8x7.services.ai.azure.com/api/projects/fwa-workshop",
        "FWA_RESOURCE_ENDPOINT": "https://fwa-z9y8x7.cognitiveservices.azure.com/",
        "FWA_APPINSIGHTS_CONNECTION_STRING": CONNECTION_STRING,
    }

    code = scan.main(["--mask", "--literal-env", "FWA_PROJECT_ENDPOINT", "--literal-env", "FWA_RESOURCE_ENDPOINT",
                      "--literal-env", "FWA_APPINSIGHTS_CONNECTION_STRING"], environ=environ)

    assert code == 0
    lines = capsys.readouterr().out.splitlines()
    assert all(line.startswith("::add-mask::") for line in lines)
    masked = {line.removeprefix("::add-mask::") for line in lines}
    # Both the case as given and lower case: masking is case-sensitive.
    assert {"FWA-z9y8x7", "fwa-z9y8x7", "fwa-z9y8x7.cognitiveservices.azure.com", "fwa-z9y8x7.services.ai.azure.com"} <= masked
    assert "00000000-0000-0000-0000-000000000000" in masked
    assert "eastus2-0.in.applicationinsights.azure.com" in masked
    assert len(lines) == len(masked)


@pytest.mark.parametrize("newline", ["\n", "\r\n", "\r"])
def test_mask_masks_a_value_with_a_line_break_line_by_line(capsys, newline):
    """A value with a line break would split its ::add-mask:: line, and GitHub would print the rest."""
    environ = {"FWA_X": f"first-half-secret{newline}second-half-secret"}

    assert scan.main(["--mask", "--literal-env", "FWA_X"], environ=environ) == 0

    out = capsys.readouterr().out
    lines = out.splitlines()
    assert all(line.startswith("::add-mask::") for line in lines)
    assert {line.removeprefix("::add-mask::") for line in lines} == {"first-half-secret", "second-half-secret"}
    assert "\r" not in out


def test_the_scan_finds_each_line_of_a_value_with_a_line_break(tmp_path):
    (tmp_path / "a.txt").write_text("ok\nthe second-half-secret leaked\n", encoding="utf-8")

    found = scan.scan(tmp_path, [("FWA_X", "first-half-secret\nsecond-half-secret")])

    assert found == ["a.txt:2: the value of FWA_X"]


def test_mask_with_nothing_set_prints_nothing_and_succeeds(capsys):
    assert scan.main(["--mask", "--literal-env", "FWA_PROJECT_ENDPOINT"], environ={}) == 0
    assert capsys.readouterr().out == ""


def test_a_directory_is_required_unless_masking(capsys):
    assert scan.main(["--literal-env", "X"], environ={}) == 2


def test_the_evaluations_polling_limit_is_30_minutes():
    # The live job's timeout is 45 minutes (WorkflowShapeTests): a slow evaluation ends here first,
    # so the scan and the upload still run.
    import inspect

    from fwa_eval import run_eval

    assert inspect.signature(run_eval.run).parameters["timeout_seconds"].default == 1800.0


def test_the_scan_imports_only_the_standard_library():
    # The masking step runs it with the runner image's Python, before setup-python and pip install.
    import ast
    import sys
    from pathlib import Path

    tree = ast.parse(Path(scan.__file__).read_text(encoding="utf-8"))
    modules = {a.name.split(".")[0] for n in ast.walk(tree) if isinstance(n, ast.Import) for a in n.names}
    modules |= {n.module.split(".")[0] for n in ast.walk(tree) if isinstance(n, ast.ImportFrom) and n.module}
    assert modules - {"__future__"} <= set(sys.stdlib_module_names)


# A connection string's region labels and generic Azure suffixes are not identifiers: the report's
# price note names the region (eastus2), and must not be a finding.
REGIONAL_CONNECTION_STRING = (
    "InstrumentationKey=00000000-0000-0000-0000-000000000000;"
    "IngestionEndpoint=https://eastus2-0.in.applicationinsights.azure.com/;"
    "LiveEndpoint=https://eastus2.livediagnostics.monitor.azure.com/;"
    "ApplicationId=11111111-1111-1111-1111-111111111111;"
    "Location=eastus2;"
    "EndpointSuffix=applicationinsights.azure.com"
)


def test_a_connection_strings_region_labels_and_generic_suffixes_are_not_literals():
    values = {v for _, v in scan.literals(["C"], {"C": REGIONAL_CONNECTION_STRING})}

    assert not {"eastus2", "eastus2-0", "applicationinsights.azure.com"} & values
    # What does identify the resource is kept.
    assert {"00000000-0000-0000-0000-000000000000", "11111111-1111-1111-1111-111111111111",
            "eastus2-0.in.applicationinsights.azure.com", "eastus2.livediagnostics.monitor.azure.com"} <= values


@pytest.mark.parametrize("label", ["westeurope-5", "westus-0", "australiaeast", "southeastasia", "uksouth", "centralus", "swedencentral"])
def test_any_region_label_of_a_connection_strings_host_is_skipped(label):
    values = {v for _, v in scan.literals(["C"], {"C": f"InstrumentationKey=k;IngestionEndpoint=https://{label}.in.applicationinsights.azure.com/"})}

    assert label not in values
    assert f"{label}.in.applicationinsights.azure.com" in values


def test_a_connection_strings_host_label_that_is_not_a_region_is_kept():
    values = {v for _, v in scan.literals(["C"], {"C": "InstrumentationKey=k;IngestionEndpoint=https://appi-z9y8x7.in.applicationinsights.azure.com/"})}

    assert "appi-z9y8x7" in values


def test_a_report_naming_the_region_passes_with_the_connection_string_as_a_literal(tmp_path, capsys):
    (tmp_path / "report.md").write_text("Prices read 2026-10-04 (Global Standard, eastus2).\nRegion: eastus2-0\n", encoding="utf-8")

    assert scan.main([str(tmp_path), "--literal-env", "C"], environ={"C": REGIONAL_CONNECTION_STRING}) == 0


@pytest.mark.parametrize("leak", [
    "key 00000000-0000-0000-0000-000000000000",
    "app 11111111-1111-1111-1111-111111111111",
    "sent to eastus2-0.in.applicationinsights.azure.com",
])
def test_the_connection_strings_real_identifiers_still_fail(tmp_path, leak):
    (tmp_path / "t.json").write_text(leak, encoding="utf-8")

    assert scan.main([str(tmp_path), "--literal-env", "C"], environ={"C": REGIONAL_CONNECTION_STRING}) == 1


def test_the_connection_string_s_literal_finding_is_by_value_not_only_by_pattern(tmp_path, capsys):
    (tmp_path / "t.json").write_text("sent to EASTUS2-0.IN.APPLICATIONINSIGHTS.AZURE.COM", encoding="utf-8")

    scan.main([str(tmp_path), "--literal-env", "C"], environ={"C": REGIONAL_CONNECTION_STRING})

    assert "the value of C" in capsys.readouterr().err


def test_mask_skips_a_connection_strings_region_labels(capsys):
    assert scan.main(["--mask", "--literal-env", "C"], environ={"C": REGIONAL_CONNECTION_STRING}) == 0

    masked = {line.removeprefix("::add-mask::") for line in capsys.readouterr().out.splitlines()}
    assert not {"eastus2", "eastus2-0", "applicationinsights.azure.com"} & masked
    assert "00000000-0000-0000-0000-000000000000" in masked


def test_an_endpoints_resource_name_is_still_a_literal_even_if_it_looked_like_a_region():
    # Only a connection string's labels are skipped: an endpoint's first label is its resource name.
    values = {v for _, v in scan.literals(["E"], {"E": "https://eastwing.services.ai.azure.com/"})}

    assert "eastwing" in values
