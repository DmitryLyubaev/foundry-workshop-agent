"""The cloud evaluation, against a fake of the project's OpenAI client: no Azure call is made.

The fake returns the openai package's own response types, so the attributes run_eval reads are
the ones the real client returns.
"""

from __future__ import annotations

import json
import typing
from types import SimpleNamespace

import pytest
from azure.ai.projects import models as project_models
from openai.types.eval_create_params import DataSourceConfigCustom
from openai.types.evals.create_eval_jsonl_run_data_source_param import (
    CreateEvalJSONLRunDataSourceParam,
    SourceFileContent,
    SourceFileContentContent,
)
from openai.types.evals.runs.output_item_list_response import OutputItemListResponse, Result

from conftest import RECORDED
from fwa_eval import dataset, run_eval

ENDPOINT = "https://example.invalid/api/projects/example"


class FakeEvals:
    """client.evals: records what it was sent, and finishes the run after `polls` retrievals."""

    def __init__(self, *, polls=2, final_status="completed", results=None, errored=0):
        self.created = []
        self.runs = SimpleNamespace(
            create=self._create_run, retrieve=self._retrieve,
            output_items=SimpleNamespace(list=self._list_items),
        )
        self.run_created = []
        self.retrievals = 0
        self._polls = polls
        self._final = final_status
        self._results = results
        self._errored = errored

    def create(self, **kwargs):
        self.created.append(kwargs)
        return SimpleNamespace(id="eval_fake")

    def _create_run(self, eval_id, **kwargs):
        self.run_created.append((eval_id, kwargs))
        return SimpleNamespace(id="run_fake", status="queued")

    def _retrieve(self, run_id, *, eval_id):
        assert (run_id, eval_id) == ("run_fake", "eval_fake")
        self.retrievals += 1
        status = self._final if self.retrievals > self._polls else "in_progress"
        counts = SimpleNamespace(total=4, passed=3, failed=1, errored=self._errored)
        return SimpleNamespace(id=run_id, status=status, result_counts=counts, error=None,
                               report_url="https://example.invalid/report")

    def _list_items(self, run_id, *, eval_id):
        assert (run_id, eval_id) == ("run_fake", "eval_fake")
        items = self.run_created[0][1]["data_source"]["source"]["content"]
        out = []
        for i, entry in enumerate(items):
            item = entry["item"]
            results = (self._results or _default_results)(i, item)
            out.append(OutputItemListResponse.model_construct(
                id=f"item_{i}", datasource_item=item, datasource_item_id=i, eval_id=eval_id,
                object="eval.run.output_item", results=results, run_id=run_id, status="completed",
            ))
        # Out of order, as a service may page them: the rows must still land on their own transcripts.
        return list(reversed(out))


def _default_results(i, item):
    return [
        Result.model_construct(name="tool_call_accuracy", passed=True, score=4.0 + i % 2, type="azure_ai_evaluator"),
        Result.model_construct(name="task_adherence", passed=i % 2 == 0, score=float(i % 2 == 0), type="azure_ai_evaluator"),
        Result.model_construct(name="intent_resolution", passed=True, score=5.0, type="azure_ai_evaluator"),
    ]


@pytest.fixture
def dataset_path(tmp_path):
    path = tmp_path / "dataset.jsonl"
    dataset.build(RECORDED, out_path=path)
    return path


def _run(dataset_path, evals, **kwargs):
    client = SimpleNamespace(evals=evals)
    sleeps = []
    result = run_eval.run(
        ENDPOINT, "gpt-5.6-luna", dataset_path, object(),
        client=client, sleep=sleeps.append, **kwargs,
    )
    return result, sleeps


def test_one_evaluation_with_the_three_evaluators_and_the_judge(dataset_path):
    evals = FakeEvals()
    _run(dataset_path, evals)

    (created,) = evals.created
    criteria = created["testing_criteria"]
    assert [c["evaluator_name"] for c in criteria] == [
        "builtin.tool_call_accuracy", "builtin.task_adherence", "builtin.intent_resolution",
    ]
    for c in criteria:
        assert c["type"] == "azure_ai_evaluator"
        assert c["initialization_parameters"] == {"deployment_name": "gpt-5.6-luna"}
    assert criteria[0]["data_mapping"] == {
        "query": "{{item.query}}", "tool_definitions": "{{item.tool_definitions}}",
        "tool_calls": "{{item.tool_calls}}", "response": "{{item.response}}",
    }
    for c in criteria[1:]:
        assert c["data_mapping"] == {
            "query": "{{item.query}}", "response": "{{item.response}}", "tool_definitions": "{{item.tool_definitions}}",
        }
    config = created["data_source_config"]
    assert config["type"] == "custom"
    assert set(config["item_schema"]["required"]) == {"query", "response", "tool_definitions"}


def test_the_run_sends_every_dataset_row_inline(dataset_path):
    evals = FakeEvals()
    _run(dataset_path, evals)

    (eval_id, kwargs) = evals.run_created[0]
    assert eval_id == "eval_fake"
    source = kwargs["data_source"]
    assert source["type"] == "jsonl"
    assert source["source"]["type"] == "file_content"
    rows = [json.loads(line) for line in dataset_path.read_text(encoding="utf-8").splitlines()]
    assert [entry["item"] for entry in source["source"]["content"]] == rows


def _keys_conform(value: dict, typed) -> None:
    hints = typing.get_type_hints(typed)
    assert set(value) <= set(hints), set(value) - set(hints)
    assert typed.__required_keys__ <= set(value)


def test_what_is_sent_conforms_to_the_packages_types(dataset_path):
    """The dicts sent match the TypedDicts azure-ai-projects and openai define for these calls."""
    evals = FakeEvals()
    _run(dataset_path, evals)

    created = evals.created[0]
    for criterion in created["testing_criteria"]:
        _keys_conform(criterion, project_models.TestingCriterionAzureAIEvaluator)
    _keys_conform(created["data_source_config"], DataSourceConfigCustom)
    source = evals.run_created[0][1]["data_source"]
    _keys_conform(source, CreateEvalJSONLRunDataSourceParam)
    _keys_conform(source["source"], SourceFileContent)
    _keys_conform(source["source"]["content"][0], SourceFileContentContent)


def test_it_polls_until_the_run_completes_and_returns_the_scores_per_row(dataset_path):
    evals = FakeEvals(polls=2)
    result, sleeps = _run(dataset_path, evals, poll_seconds=7)

    assert evals.retrievals == 3
    assert sleeps == [7, 7]
    assert result["status"] == "completed"
    assert result["judge"] == "gpt-5.6-luna"
    assert result["evaluators"] == [
        {"name": "tool_call_accuracy", "evaluator": "builtin.tool_call_accuracy", "preview": False},
        {"name": "task_adherence", "evaluator": "builtin.task_adherence", "preview": True},
        {"name": "intent_resolution", "evaluator": "builtin.intent_resolution", "preview": True},
    ]
    rows = result["rows"]
    assert [(r["scenario_id"], r["engine"], r["pass"]) for r in rows] == [
        ("s01", "claude", 1), ("s01", "gpt", 1), ("s02", "claude", 1), ("s03", "claude", 1),
    ]
    assert rows[1]["scores"] == {
        "tool_call_accuracy": {"score": 5.0, "passed": True},
        "task_adherence": {"score": 0.0, "passed": False},
        "intent_resolution": {"score": 5.0, "passed": True},
    }
    assert result["result_counts"] == {"total": 4, "passed": 3, "failed": 1, "errored": 0}
    # Nothing that names the project or the run is kept: no ids, no report URL.
    text = json.dumps(result)
    assert "example.invalid" not in text and "eval_fake" not in text and "run_fake" not in text


def test_a_row_the_judge_could_not_score_has_no_score(dataset_path):
    def results(i, item):
        if i == 0:
            return []
        return [Result.model_construct(name="tool_call_accuracy", passed=False, score=float("nan"), type="azure_ai_evaluator")]

    result, _ = _run(dataset_path, FakeEvals(results=results))

    assert result["rows"][0]["scores"] == {
        "tool_call_accuracy": {"score": None, "passed": None},
        "task_adherence": {"score": None, "passed": None},
        "intent_resolution": {"score": None, "passed": None},
    }
    assert result["rows"][1]["scores"]["tool_call_accuracy"] == {"score": None, "passed": False}


def test_a_failed_run_raises_without_naming_anything(dataset_path):
    with pytest.raises(RuntimeError) as raised:
        _run(dataset_path, FakeEvals(final_status="failed"))
    assert "failed" in str(raised.value)
    assert "example.invalid" not in str(raised.value)


def test_a_run_that_never_finishes_times_out(dataset_path):
    with pytest.raises(TimeoutError):
        _run(dataset_path, FakeEvals(polls=10_000), poll_seconds=5, timeout_seconds=20)


def test_without_a_client_it_builds_one_from_the_project_keyless(dataset_path, monkeypatch):
    seen = {}
    evals = FakeEvals()

    class FakeProject:
        def __init__(self, endpoint, credential, **kwargs):
            seen["endpoint"], seen["credential"], seen["kwargs"] = endpoint, credential, kwargs

        def get_openai_client(self, **kwargs):
            seen["openai_kwargs"] = kwargs
            return SimpleNamespace(evals=evals)

    monkeypatch.setattr(run_eval, "AIProjectClient", FakeProject)
    credential = object()

    run_eval.run(ENDPOINT, "gpt-5.6-luna", dataset_path, credential, sleep=lambda s: None)

    assert seen == {"endpoint": ENDPOINT, "credential": credential, "kwargs": {}, "openai_kwargs": {}}
    assert len(evals.created) == 1


def test_the_credential_is_the_azure_cli(monkeypatch):
    """Locally the owner's az sign-in; in CI the azure/login OIDC sign-in of the same CLI. No key."""
    from azure.identity import AzureCliCredential

    assert isinstance(run_eval.default_credential(), AzureCliCredential)


@pytest.mark.parametrize(
    "text",
    [
        "principal 11111111-1111-1111-1111-111111111111 is not allowed",
        "POST https://fwa-x1y2.services.ai.azure.com/api/projects/p failed",
        "host fwa-x1y2.cognitiveservices.azure.com refused",
        "token Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl expired",
        "ask owner@example.com",
        "login.microsoftonline.com timed out",
    ],
)
def test_error_text_is_redacted(text):
    redacted = run_eval.redact(text)
    for secret in ("11111111-1111", "fwa-x1y2", "services.ai.azure.com", "eyJ", "owner@example.com", "microsoftonline"):
        assert secret not in redacted
