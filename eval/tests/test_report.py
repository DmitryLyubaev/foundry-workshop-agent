"""The Markdown report: its sections and labels, its figures, and that nothing identifying gets in."""

from __future__ import annotations

import datetime as dt
import hashlib
import re

import pytest

from conftest import RECORDED, transcript, write, write_freeze, write_study
from fwa_eval import analysis, prices, report
from fwa_eval.analysis import compare

RENDERED_ON = dt.date(2026, 10, 8)

SCORES = {
    "status": "completed",
    "judge": "gpt-5.6-luna",
    "evaluators": [
        {"name": "tool_call_accuracy", "evaluator": "builtin.tool_call_accuracy", "preview": False},
        {"name": "task_adherence", "evaluator": "builtin.task_adherence", "preview": True},
        {"name": "intent_resolution", "evaluator": "builtin.intent_resolution", "preview": True},
    ],
    "rows": [
        {"scenario_id": "s01", "engine": "gpt", "pass": 1, "scores": {
            "tool_call_accuracy": {"score": 5.0, "passed": True},
            "task_adherence": {"score": 1.0, "passed": True},
            "intent_resolution": {"score": 4.0, "passed": True},
        }},
        {"scenario_id": "s01", "engine": "claude", "pass": 1, "scores": {
            "tool_call_accuracy": {"score": 3.0, "passed": False},
            "task_adherence": {"score": 0.0, "passed": False},
            "intent_resolution": {"score": None, "passed": None},
        }},
        {"scenario_id": "s02", "engine": "claude", "pass": 1, "scores": {
            "tool_call_accuracy": {"score": 4.0, "passed": True},
            "task_adherence": {"score": 1.0, "passed": True},
            "intent_resolution": {"score": 5.0, "passed": True},
        }},
    ],
    "result_counts": {"total": 3, "passed": 2, "failed": 1, "errored": 0},
}


@pytest.fixture
def recorded_report(freeze):
    return report.render(compare(RECORDED, freeze_path=freeze), SCORES, RECORDED, rendered_on=RENDERED_ON)


def _section(text: str, heading: str) -> str:
    start = text.index(heading)
    end = text.find("\n## ", start + len(heading))
    return text[start:end if end != -1 else None]


def test_the_report_has_every_section(recorded_report):
    assert recorded_report.startswith("# Foundry workshop agent: study report\n")
    for heading in (
        "## Task success",
        "## Tokens, cost and time per task",
        "## Tool calls, gate violations and outcomes",
        "## Foundry evaluator scores",
        "## The pre-registered comparison",
        "## Limits",
    ):
        assert f"\n{heading}" in recorded_report, heading


def test_the_header_gives_the_date_n_and_the_freeze_hash(recorded_report, freeze):
    assert "**Date:** 2026-10-07 (runs, UTC); rendered 2026-10-08" in recorded_report
    assert "**Scenarios compared (n):** 1" in recorded_report
    short = hashlib.sha256(freeze.read_bytes()).hexdigest()[:12]
    assert f"**Freeze:** `{short}`, frozen on 2026-10-06" in recorded_report
    # One pass where the rule asks for three: said, so a smoke run is never read as the study.
    # Six runs of the 120 the freeze asks for: said, so a smoke run is never read as the study.
    assert "**Runs:** 6 of the 120 the rule asks for (20 scenarios × 3 passes × 2 engines)" in recorded_report
    assert "a smoke run, not the study" in recorded_report


def test_task_success_per_engine(recorded_report):
    section = _section(recorded_report, "## Task success")
    # GPT: three runs, two infrastructure errors dropped, one kept and successful.
    assert "| GPT (`gpt-5.6-luna`) | 3 | 2 | 1 | 1 | 100.0% |" in section
    # Claude: three runs kept, none successful.
    assert "| Claude (`claude-haiku-4-5`) | 3 | 0 | 3 | 0 | 0.0% |" in section


def test_tokens_cost_and_time_per_task(recorded_report):
    section = _section(recorded_report, "## Tokens, cost and time per task")
    # GPT's one scored run, s01: 5,700 in, 130 out, $0.001296.
    assert "| GPT | 5,700 | 130 | $0.0013 | $0.0013 | 9.5 s |" in section
    # Claude: means of 3,900/1,300/3,500 in and 125/30/1,049 out; $0.01472 over the three; 108.3 s.
    assert "| Claude | 2,900 | 401 | $0.0049 | $0.0147 | 108.3 s |" in section
    # The infrastructure-error runs on their own: GPT's s02 (no call) and s03 (1,100 in, 20 out,
    # $0.000244 billed), kept out of the scored runs' figures.
    infra = section[section.index("| Engine | Infrastructure-error runs |"):]
    assert "| GPT | 2 | 1,100 | 20 | $0.0002 |" in infra
    assert "| Claude | 0 | 0 | 0 | $0.0000 |" in infra
    assert prices.SOURCE in recorded_report
    assert prices.READ_ON in recorded_report


def test_tool_calls_gate_violations_and_outcomes_by_kind(recorded_report):
    section = _section(recorded_report, "## Tool calls, gate violations and outcomes")
    assert "| GPT | 2.00 | 0 |" in section
    assert "| Claude | 1.33 | 0 |" in section
    assert "| completed | 1 | 1 |" in section
    assert "| time_limit | 0 | 1 |" in section
    assert "| truncated | 0 | 1 |" in section
    assert "| service_error (infrastructure) | 1 | 0 |" in section
    assert "| infra_error (infrastructure) | 1 | 0 |" in section
    # The cut-off press is counted and explained, not read as a call that did nothing.
    assert "1 tool call (1 approved destructive press) was cut off" in section
    assert "may still have" in section


def test_the_evaluator_scores_label_the_preview_ones(recorded_report):
    section = _section(recorded_report, "## Foundry evaluator scores")
    assert "Judge: `gpt-5.6-luna`" in section
    assert "| Tool call accuracy | 5.00 (1 of 1) | 100.0% | 3.50 (2 of 2) | 50.0% |" in section
    assert "| Task adherence (preview) |" in section
    assert "| Intent resolution (preview) | 4.00 (1 of 1) | 100.0% | 5.00 (1 of 2) | 100.0% |" in section
    assert "Tool call accuracy (preview)" not in section
    assert "descriptive" in section


def test_without_scores_the_section_says_so(freeze):
    text = report.render(compare(RECORDED, freeze_path=freeze), None, RECORDED, rendered_on=RENDERED_ON)
    assert "The evaluators were not run" in _section(text, "## Foundry evaluator scores")


def test_the_verdict_as_rendered(tmp_path, freeze):
    study = write_study(tmp_path / "study", {f"s{i:02d}": ([1, 1, 1], [1, 1, 1]) for i in range(1, 21)})
    text = report.render(compare(study, freeze_path=freeze), SCORES, study, rendered_on=RENDERED_ON)
    section = _section(text, "## The pre-registered comparison")
    assert "**Verdict: inconclusive at 20 scenarios.**" in section
    assert "C1 = GPT − Claude = +0.000 (95% interval +0.000 to +0.000)" in section
    assert "threshold 0.10, seed 20261004, 10,000 resamples, 3 passes" in section
    assert "a smoke run" not in text

    better = write_study(tmp_path / "better", {f"s{i:02d}": ([1, 1, 1], [0, 0, 0]) for i in range(1, 6)})
    text = report.render(compare(better, freeze_path=freeze), None, better, rendered_on=RENDERED_ON)
    assert "**Verdict: difference.** GPT's task success is higher, over 5 scenarios." in text


def test_the_limits(recorded_report):
    section = _section(recorded_report, "## Limits")
    assert "client" in section and "not inside Foundry Agent Service" in section
    assert "No claim that either model is better" in section
    assert "other apps, other tasks or production use" in section
    assert "preview" in section
    assert "bias" in section


def test_a_study_with_one_infra_error_is_labelled_the_study_with_the_drop_reported(tmp_path, freeze):
    """20 × 3 × 2 runs, one of them a Claude service error: the study, not a smoke run."""
    study = write_study(tmp_path / "study", {f"s{i:02d}": ([1, 1, 0], [1, 0, 0]) for i in range(1, 21)})
    write(study, transcript("s07", "claude", 2, success=False, outcome="service_error", infra=True, final_reply=None))

    text = report.render(compare(study, freeze_path=freeze), None, study, rendered_on=RENDERED_ON)

    assert "**Runs:** 120 of the 120 the rule asks for (20 scenarios × 3 passes × 2 engines), counting runs dropped for an infrastructure error." in text
    assert "smoke run" not in text
    section = _section(text, "## The pre-registered comparison")
    assert "Dropped from the pairs for an infrastructure error (spec §5.4): 1 run: Claude s07 pass 2." in section
    assert "over 20 scenarios" in section


def test_a_study_without_infra_errors_says_none_was_dropped(tmp_path, freeze):
    study = write_study(tmp_path / "study", {f"s{i:02d}": ([1, 1, 1], [1, 1, 1]) for i in range(1, 21)})
    text = report.render(compare(study, freeze_path=freeze), None, study, rendered_on=RENDERED_ON)
    assert "No run was dropped for an infrastructure error." in text


def test_an_unfrozen_run_says_so(tmp_path, monkeypatch):
    study = write_study(tmp_path / "study", {"s01": ([1], [0])})
    monkeypatch.setattr(analysis, "DEFAULT_FREEZE", tmp_path / "none" / "freeze.json")
    text = report.render(compare(study), None, study, rendered_on=RENDERED_ON)
    assert "**Freeze:** none: these transcripts are not a frozen study run" in text


@pytest.mark.parametrize(
    ("change", "said"),
    [
        ({"settings_sha": "8" * 64}, "their settings differ from it"),
        ({"instructions_sha": "9" * 64}, "their instructions differ from it"),
        ({"tools_sha": "7" * 64}, "the tools differ from it"),
        ({"scenarios": ["s02"]}, "they hold scenarios it does not list"),
    ],
)
def test_transcripts_that_do_not_match_the_freeze_are_flagged_on_the_verdict(tmp_path, change, said):
    study = write_study(tmp_path / "study", {"s01": ([1, 1, 1], [0, 0, 0])})
    other = write_freeze(tmp_path / "f" / "freeze.json", **change)

    text = report.render(compare(study, freeze_path=other), None, study, rendered_on=RENDERED_ON)

    assert f"were not run under this freeze ({said})" in text
    assert "**Verdict: difference (transcripts do not match the freeze).** GPT's task success is higher" in text

    inconclusive = write_study(tmp_path / "even", {"s01": ([1, 1, 1], [1, 1, 1])})
    text = report.render(compare(inconclusive, freeze_path=other), None, inconclusive, rendered_on=RENDERED_ON)
    assert "**Verdict: inconclusive at 1 scenario (transcripts do not match the freeze).**" in text


def test_a_matching_freeze_leaves_the_verdict_unqualified(tmp_path, freeze):
    study = write_study(tmp_path / "study", {"s01": ([1, 1, 1], [0, 0, 0])})
    text = report.render(compare(study, freeze_path=freeze), None, study, rendered_on=RENDERED_ON)
    assert "do not match the freeze" not in text
    assert "**Verdict: difference.**" in text


# --- Review Focus 4: nothing identifying reaches the report ----------------------------------

_GUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.IGNORECASE)
_URL = re.compile(r"[a-z][a-z0-9+.-]*://", re.IGNORECASE)
_HOST = re.compile(
    r"[\w-]+(\.[\w-]+)*\.(azure\.com|azure\.net|windows\.net|microsoft\.com|microsoftonline\.com|anthropic\.com|azurewebsites\.net)",
    re.IGNORECASE,
)
_LONG_HEX = re.compile(r"[0-9a-f]{32,}", re.IGNORECASE)
_LONG_TOKEN = re.compile(r"[A-Za-z0-9+/_-]{40,}={0,2}")
_EMAIL = re.compile(r"[\w.+-]+@[\w-]+(\.[\w-]+)+")
_KEY_WORDS = re.compile(r"bearer|eyJ|instrumentationkey|accountkey|api[-_]?key|sharedaccess|sk-[a-z0-9]", re.IGNORECASE)


def _identifiers(text: str) -> list[str]:
    found = []
    for pattern in (_GUID, _URL, _HOST, _LONG_HEX, _LONG_TOKEN, _EMAIL, _KEY_WORDS):
        found += [m.group(0) for m in pattern.finditer(text)]
    return found


def test_the_scan_catches_what_it_is_for():
    assert _identifiers("22222222-2222-2222-2222-222222222222")
    assert _identifiers("see https://x.example")
    assert _identifiers("fwa-ab12.services.ai.azure.com")
    assert _identifiers("a" * 64)
    assert _identifiers("Bearer abc")
    assert _identifiers("someone@example.com")
    assert not _identifiers("| GPT | 5,700 | 130 | $0.0013 | `abcdef123456` | s01 | gpt-5.6-luna |")


def _planted_study(directory):
    """Transcripts whose every free-text field carries an identifier, as a leaky SDK error would."""
    planted = {
        "infraMessage": "Azure.Identity failed for 11111111-1111-1111-1111-111111111111 at https://fwa-x9y8.services.ai.azure.com/api/projects/fwa-proj-x9y8",
        "error": "401 from fwa-x9y8.cognitiveservices.azure.com: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig; owner@example.com",
        "finalReply": "Ask owner@example.com; key 0123456789abcdef0123456789abcdef.",
    }
    for i, engine in enumerate(("gpt", "claude")):
        t = transcript("s01", engine, 1, success=bool(i))
        t.update(planted)
        t["deployment"] = "fwa-gpt-x9y8"
        t["tools"][0]["message"] = planted["error"]
        t["check"] = {"passed": bool(i), "failures": [planted["infraMessage"]] * (1 - i)}
        t["success"] = bool(i)
        write(directory, t)
    # A run under a model whose name is the owner's own deployment name, not a public model.
    t = transcript("s02", "gpt", 1, outcome="service_error", infra=True, final_reply=None)
    t.update(planted)
    t["model"] = "fwa-gpt-x9y8"
    t["finalReply"] = None
    write(directory, t)
    write(directory, transcript("s02", "claude", 1))
    return directory


def test_the_report_holds_no_guid_endpoint_or_key(tmp_path, freeze):
    study = _planted_study(tmp_path / "study-0123456789ab")
    scores = {**SCORES, "judge": "fwa-judge-x9y8", "report_url": "https://ai.azure.com/x/11111111-1111-1111-1111-111111111111"}

    text = report.render(compare(study, freeze_path=freeze), scores, study, rendered_on=RENDERED_ON)

    assert _identifiers(text) == []
    assert "x9y8" not in text
    # The owner's own deployment names are not public model names, so they are not printed either.
    assert "(not in the price list)" in text


def test_the_recorded_report_holds_no_guid_endpoint_or_key(recorded_report):
    assert _identifiers(recorded_report) == []


def test_prices_are_fixed_with_source_and_date():
    assert prices.PRICES["gpt-5.6-luna"] == prices.Price(0.20, 1.20)
    assert prices.PRICES["claude-haiku-4-5"] == prices.Price(1.00, 5.00)
    assert prices.cost("gpt-5.6-luna", 1_000_000, 1_000_000) == pytest.approx(1.40)
    assert prices.cost("claude-haiku-4-5", 2_000, 100) == pytest.approx(0.0025)
    assert prices.cost("unknown", 10, 10) is None
    assert re.fullmatch(r"\d{4}-\d{2}-\d{2}", prices.READ_ON)
    assert prices.SOURCE
