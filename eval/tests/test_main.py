"""The command line, offline: the eval command's Azure call is replaced by a fake."""

from __future__ import annotations

import datetime as dt
import io
import json
import shutil

import pytest

from conftest import RECORDED, write_freeze
from fwa_eval import __main__ as cli
from fwa_eval import analysis, report, run_eval


@pytest.fixture
def study(tmp_path):
    directory = tmp_path / "study"
    shutil.copytree(RECORDED, directory)
    return directory


def _main(*argv):
    out, err = io.StringIO(), io.StringIO()
    code = cli.main(list(map(str, argv)), out=out, err=err)
    return code, out.getvalue(), err.getvalue()


@pytest.fixture(autouse=True)
def no_default_freeze(tmp_path, monkeypatch):
    """No test reads the repository's own scenarios/freeze.json, whether or not it exists yet."""
    monkeypatch.setattr(analysis, "DEFAULT_FREEZE", tmp_path / "no-default" / "freeze.json")


def test_dataset_writes_beside_the_transcripts(study):
    code, out, _ = _main("dataset", study)
    assert code == 0
    assert "4 rows" in out
    assert len((study / "dataset.jsonl").read_text(encoding="utf-8").splitlines()) == 4


def test_analyse_prints_the_comparison(study, freeze, tmp_path):
    code, out, _ = _main("analyse", study, "--freeze", freeze, "--out", tmp_path / "a.json")
    assert code == 0
    assert json.loads(out) == analysis.compare(study, freeze_path=freeze)
    assert json.loads((tmp_path / "a.json").read_text(encoding="utf-8"))["n"] == 1


def test_report_reads_the_scores_beside_the_transcripts(study, freeze, monkeypatch):
    scores = {"status": "completed", "judge": "gpt-5.6-luna", "evaluators": [], "rows": [], "result_counts": {}}
    (study / cli.SCORES_FILE).write_text(json.dumps(scores), encoding="utf-8")

    code, out, _ = _main("report", study, "--freeze", freeze)

    assert code == 0
    written = (study / cli.REPORT_FILE).read_text(encoding="utf-8")
    assert out == written
    assert "Judge: `gpt-5.6-luna`" in written
    today = dt.datetime.now(dt.UTC).date()
    assert written == report.render(analysis.compare(study, freeze_path=freeze), scores, study, rendered_on=today)


def test_report_without_scores_says_the_evaluators_did_not_run(study, freeze):
    code, out, _ = _main("report", study, "--freeze", freeze)
    assert code == 0
    assert "The evaluators were not run" in out


def test_eval_needs_the_project_endpoint(study, monkeypatch):
    monkeypatch.delenv(cli.ENDPOINT_VARIABLE, raising=False)
    code, _, err = _main("eval", study)
    assert code == 2
    assert cli.ENDPOINT_VARIABLE in err


def test_eval_scores_the_dataset_keyless_and_writes_the_scores(study, tmp_path, monkeypatch):
    monkeypatch.setenv(cli.ENDPOINT_VARIABLE, "https://example.invalid/api/projects/example")
    monkeypatch.delenv(cli.JUDGE_VARIABLE, raising=False)
    credential = object()
    monkeypatch.setattr(run_eval, "default_credential", lambda: credential)
    seen = {}

    def fake_run(endpoint, judge, dataset_path, cred):
        seen.update(endpoint=endpoint, judge=judge, rows=len(dataset_path.read_text(encoding="utf-8").splitlines()), cred=cred)
        return {"status": "completed", "judge": judge, "evaluators": [], "rows": [], "result_counts": {}}

    monkeypatch.setattr(run_eval, "run", fake_run)

    code, out, err = _main("eval", study, "--freeze", write_freeze(tmp_path / "freeze.json"))

    assert (code, err) == (0, "")
    assert seen == {"endpoint": "https://example.invalid/api/projects/example", "judge": "gpt-5.6-luna", "rows": 4, "cred": credential}
    assert json.loads((study / cli.SCORES_FILE).read_text(encoding="utf-8"))["judge"] == "gpt-5.6-luna"
    # The endpoint is never echoed.
    assert "example.invalid" not in out


def test_eval_takes_the_judge_from_the_gpt_deployment_variable(study, tmp_path, monkeypatch):
    monkeypatch.setenv(cli.ENDPOINT_VARIABLE, "https://example.invalid/p")
    monkeypatch.setenv(cli.JUDGE_VARIABLE, "gpt-judge")
    monkeypatch.setattr(run_eval, "default_credential", lambda: None)
    judges = []
    monkeypatch.setattr(run_eval, "run", lambda e, judge, d, c: judges.append(judge) or {"rows": []})

    _main("eval", study)
    _main("eval", study, "--judge", "other")

    assert judges == ["gpt-judge", "other"]


def test_eval_refuses_tools_that_are_not_the_frozen_ones(study, tmp_path, monkeypatch):
    monkeypatch.setenv(cli.ENDPOINT_VARIABLE, "https://example.invalid/p")
    monkeypatch.setattr(run_eval, "run", lambda *a: pytest.fail("must not run"))

    code, _, err = _main("eval", study, "--freeze", write_freeze(tmp_path / "freeze.json", tools_sha="4" * 64))

    assert code == 2
    assert "tools" in err


def test_a_failed_evaluation_is_reported_redacted(study, tmp_path, monkeypatch):
    monkeypatch.setenv(cli.ENDPOINT_VARIABLE, "https://example.invalid/p")
    monkeypatch.setattr(run_eval, "default_credential", lambda: None)

    def failing(*args):
        raise RuntimeError("403 for 11111111-1111-1111-1111-111111111111 on https://fwa-x9y8.services.ai.azure.com/api")

    monkeypatch.setattr(run_eval, "run", failing)

    code, _, err = _main("eval", study)

    assert code == 1
    assert "The evaluation failed: RuntimeError: 403" in err
    assert "11111111" not in err and "x9y8" not in err


@pytest.mark.parametrize("command", ["analyse", "report", "eval"])
def test_a_freeze_path_that_holds_no_file_is_an_error(study, tmp_path, monkeypatch, command):
    monkeypatch.setenv(cli.ENDPOINT_VARIABLE, "https://example.invalid/p")
    monkeypatch.setattr(run_eval, "run", lambda *a: pytest.fail("must not run"))

    code, out, err = _main(command, study, "--freeze", tmp_path / "scenarios" / "freez.json")

    assert code == 2
    assert "There is no freeze at" in err and "--freeze" in err
    assert out == ""
    assert not (study / cli.REPORT_FILE).exists()


def test_leaving_the_freeze_out_reports_without_one(study):
    code, out, _ = _main("report", study)

    assert code == 0
    assert "**Freeze:** none" in out


def test_usage_errors_exit_2():
    code, _, err = _main("nonsense")
    assert code == 2
    assert "python -m fwa_eval" in err


def test_a_directory_without_transcripts_fails_cleanly(tmp_path):
    code, _, err = _main("dataset", tmp_path)
    assert code == 1
    assert "no transcripts" in err
