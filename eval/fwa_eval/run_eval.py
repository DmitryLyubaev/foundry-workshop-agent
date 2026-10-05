"""One cloud evaluation of the dataset by Foundry's built-in agent evaluators, keyless.

It goes through the project's OpenAI client (azure-ai-projects 2.x, `get_openai_client()`): one
`evals.create` with the three evaluators as `azure_ai_evaluator` testing criteria, one
`evals.runs.create` with the dataset rows inline (`jsonl`, `file_content`), then polling
`evals.runs.retrieve` until the run ends, and reading `evals.runs.output_items.list`. This is the
shape of azure-ai-projects' own samples (samples/evaluations/agentic_evaluators/).

The evaluators score stored transcripts, because Microsoft's evaluation action invokes the agent
itself and cannot run the local tools (spec §7). They are descriptive: task success, decided by the
database, is the study's measure. The judge is the project's `gpt-5.6-luna` deployment.

Sign-in is Entra only, through the Azure CLI: the owner's `az login` locally, and in CI the
`azure/login` OIDC sign-in of the same CLI. No key exists to use.
"""

from __future__ import annotations

import json
import math
import re
import time
from collections.abc import Callable
from pathlib import Path

from azure.ai.projects import AIProjectClient
from azure.identity import AzureCliCredential

JUDGE_DEPLOYMENT = "gpt-5.6-luna"

# name, evaluator id, preview, the dataset fields it reads. Tool call accuracy reads the calls; the
# other two read the conversation, with the tools for context.
EVALUATORS = (
    ("tool_call_accuracy", "builtin.tool_call_accuracy", False, ("query", "tool_definitions", "tool_calls", "response")),
    ("task_adherence", "builtin.task_adherence", True, ("query", "response", "tool_definitions")),
    ("intent_resolution", "builtin.intent_resolution", True, ("query", "response", "tool_definitions")),
)

_TERMINAL = ("completed", "failed", "canceled", "cancelled")

_TEXT_OR_MESSAGES = {"anyOf": [{"type": "string"}, {"type": "array", "items": {"type": "object"}}]}
_OBJECT_OR_LIST = {"anyOf": [{"type": "object"}, {"type": "array", "items": {"type": "object"}}]}

ITEM_SCHEMA = {
    "type": "object",
    "properties": {
        "query": _TEXT_OR_MESSAGES,
        "response": _TEXT_OR_MESSAGES,
        "tool_definitions": _OBJECT_OR_LIST,
        "tool_calls": _OBJECT_OR_LIST,
        # Not mapped to any evaluator: they join each score back to its transcript.
        "scenario_id": {"type": "string"},
        "engine": {"type": "string"},
        "pass": {"type": "integer"},
        "outcome": {"type": "string"},
        "cut_off_tool_calls": {"type": "array", "items": {"type": "integer"}},
    },
    "required": ["query", "response", "tool_definitions"],
}


def default_credential() -> AzureCliCredential:
    """The Azure CLI's sign-in: the owner locally, the OIDC-federated CI identity in CI."""
    return AzureCliCredential()


def testing_criteria(judge_deployment: str) -> list[dict]:
    return [
        {
            "type": "azure_ai_evaluator",
            "name": name,
            "evaluator_name": evaluator,
            "initialization_parameters": {"deployment_name": judge_deployment},
            "data_mapping": {field: "{{item." + field + "}}" for field in fields},
        }
        for name, evaluator, _, fields in EVALUATORS
    ]


def _read_rows(dataset_path: str | Path) -> list[dict]:
    return [json.loads(line) for line in Path(dataset_path).read_text(encoding="utf-8").splitlines() if line.strip()]


def _key(item: dict) -> tuple | None:
    # The service returns the item as sent; some shapes nest it under "item".
    if isinstance(item, dict) and "item" in item and isinstance(item["item"], dict):
        item = item["item"]
    if isinstance(item, dict) and {"scenario_id", "engine", "pass"} <= set(item):
        return (item["scenario_id"], item["engine"], item["pass"])
    return None


def _score(value) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or math.isnan(value):
        return None
    return float(value)


def _evaluator_for(result_name: str) -> str | None:
    """Which of our evaluators a result belongs to: its name as given, or one that holds it."""
    names = [name for name, *_ in EVALUATORS]
    if result_name in names:
        return result_name
    for name in names:
        if name in result_name:
            return name
    return None


def run(
    project_endpoint: str,
    judge_deployment: str,
    dataset_path: str | Path,
    credential,
    *,
    client=None,
    poll_seconds: float = 5.0,
    timeout_seconds: float = 1800.0,
    sleep: Callable[[float], None] = time.sleep,
) -> dict:
    """Scores the dataset with the three evaluators and returns the per-row scores.

    `client` stands in for the project's OpenAI client in tests. What is returned keeps nothing
    that names the project or the run (no ids, no report URL), so it can be published.
    """
    rows = _read_rows(dataset_path)
    if client is None:
        client = AIProjectClient(project_endpoint, credential).get_openai_client()

    evaluation = client.evals.create(
        name="fwa-transcripts",
        data_source_config={"type": "custom", "item_schema": ITEM_SCHEMA, "include_sample_schema": False},
        testing_criteria=testing_criteria(judge_deployment),
    )
    created = client.evals.runs.create(
        evaluation.id,
        name="fwa-transcripts-run",
        data_source={"type": "jsonl", "source": {"type": "file_content", "content": [{"item": r} for r in rows]}},
    )

    waited = 0.0
    current = client.evals.runs.retrieve(created.id, eval_id=evaluation.id)
    while current.status not in _TERMINAL:
        if waited >= timeout_seconds:
            raise TimeoutError(f"The evaluation run was still '{current.status}' after {timeout_seconds:.0f} s.")
        sleep(poll_seconds)
        waited += poll_seconds
        current = client.evals.runs.retrieve(created.id, eval_id=evaluation.id)

    counts = current.result_counts
    result_counts = {k: getattr(counts, k, None) for k in ("total", "passed", "failed", "errored")}
    if current.status != "completed":
        raise RuntimeError(f"The evaluation run ended '{current.status}' (results: {result_counts}).")

    keys = [(r["scenario_id"], r["engine"], r["pass"]) for r in rows]
    scores = {k: {name: {"score": None, "passed": None} for name, *_ in EVALUATORS} for k in keys}
    for item in client.evals.runs.output_items.list(created.id, eval_id=evaluation.id):
        key = _key(item.datasource_item)
        if key is None and isinstance(item.datasource_item_id, int) and 0 <= item.datasource_item_id < len(keys):
            key = keys[item.datasource_item_id]
        if key not in scores:
            continue
        for result in item.results or []:
            name = _evaluator_for(getattr(result, "name", "") or "")
            if name is None:
                continue
            passed = getattr(result, "passed", None)
            scores[key][name] = {"score": _score(getattr(result, "score", None)), "passed": passed if isinstance(passed, bool) else None}

    return {
        "status": current.status,
        "judge": judge_deployment,
        "evaluators": [{"name": name, "evaluator": evaluator, "preview": preview} for name, evaluator, preview, _ in EVALUATORS],
        "rows": [{"scenario_id": k[0], "engine": k[1], "pass": k[2], "scores": scores[k]} for k in keys],
        "result_counts": result_counts,
    }


# What a service error may carry that must not reach a log or a file: ids, hosts, URLs, emails, tokens.
_REDACTIONS = (
    (re.compile(r"[a-z][a-z0-9+.-]*://\S+", re.IGNORECASE), "<url>"),
    (re.compile(r"\bBearer\s+\S+", re.IGNORECASE), "Bearer <token>"),
    (re.compile(r"\beyJ[\w-]*\.[\w-]*\.[\w-]*"), "<token>"),
    (re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", re.IGNORECASE), "<guid>"),
    (re.compile(r"[\w.+-]+@[\w-]+(\.[\w-]+)+"), "<email>"),
    (re.compile(r"\b[\w-]+(\.[\w-]+)*\.(azure\.com|azure\.net|windows\.net|microsoft\.com|microsoftonline\.com|anthropic\.com|azurewebsites\.net)\b", re.IGNORECASE), "<host>"),
    (re.compile(r"\b[0-9a-f]{32,}\b", re.IGNORECASE), "<hex>"),
)


def redact(text: str) -> str:
    """`text` with identifiers, hosts, URLs, emails and tokens replaced by placeholders."""
    for pattern, placeholder in _REDACTIONS:
        text = pattern.sub(placeholder, text)
    return text
