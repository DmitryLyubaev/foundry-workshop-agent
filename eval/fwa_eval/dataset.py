"""The runner's transcripts, read, and turned into the dataset Foundry's agent evaluators score.

A transcript is `<scenarioId>.<engine>.p<pass>.json`, as Workshop.Agent's Transcript record writes
it (src/Workshop.Agent/Runner/Transcript.cs). Each one becomes one JSONL row holding:

- `query`: the task, as the model was given it
- `response`: the run as messages, in the shape the agent evaluators read (an assistant message
  per model answer holding its tool calls, a tool message per result, then the final reply)
- `tool_definitions`: the six tools, from tools.json, which a .NET test holds equal to the
  canonical JSON the freeze hashes
- `tool_calls`: every tool call the model made, in order

and, for joining the scores back, `scenario_id`, `engine`, `pass`, `outcome` and
`cut_off_tool_calls`. A run marked `infraError` is left out: it says nothing about the model.
"""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path

DATASET_FILE = "dataset.jsonl"
TOOLS_FILE = Path(__file__).with_name("tools.json")

_TRANSCRIPT_NAME = re.compile(r"^(?P<scenario>[^.]+)\.(?P<engine>[^.]+)\.p(?P<pass>[1-9][0-9]*)\.json$")

_REQUIRED = (
    "scenarioId", "pass", "engine", "model", "task", "instructionsSha256", "settingsSha256", "outcome",
    "infraError", "calls", "tools", "finalReply", "gateViolations", "check", "success", "ms", "startedAt",
)

# A call with no result gave the model nothing: the run ended while it ran (`cancelled`), or the app
# or the connection failed under it (`error`). An approved destructive press is recorded `cancelled`
# before it is sent, so a near-zero time does not mean the app never did it.
_NO_RESULT_NOTE = (
    "The model was never given a result for this call: {why}. The app may have carried it out all "
    "the same; whether it did is for the end-state checks on the database, not this record, to say."
)
_WHY = {
    "cancelled": "the run ended while the call was running",
    "error": "the app or the connection failed while the call was running",
}


def _success_rule(t: dict) -> bool:
    """Task success (plan 2's ruling): the check passed, no gate violation, no infra error, completed."""
    return bool(t["check"]["passed"]) and t["gateViolations"] == 0 and not t["infraError"] and t["outcome"] == "completed"


def load_transcripts(transcripts_dir: str | Path) -> list[dict]:
    """Every transcript in the directory, ordered by scenario, engine and pass.

    Other files, such as the dataset or a report written beside them, are ignored. A transcript
    whose name disagrees with its content, that misses a field, or whose `success` breaks the rule
    is refused: the analysis must not read a figure the runner did not write.
    """
    directory = Path(transcripts_dir)
    transcripts = []
    for path in sorted(directory.glob("*.json")):
        name = _TRANSCRIPT_NAME.match(path.name)
        if name is None:
            continue
        t = json.loads(path.read_text(encoding="utf-8"))
        missing = [field for field in _REQUIRED if field not in t]
        if missing:
            raise ValueError(f"{path.name} is not a transcript: it has no {', '.join(missing)}")
        if (t["scenarioId"], t["engine"], t["pass"]) != (name["scenario"], name["engine"], int(name["pass"])):
            raise ValueError(
                f"{path.name} holds {t['scenarioId']}.{t['engine']}.p{t['pass']}: the name and the content disagree"
            )
        if bool(t["success"]) != _success_rule(t):
            raise ValueError(
                f"{t['scenarioId']}.{t['engine']}.p{t['pass']} records success {t['success']}, "
                "which its check, gate violations, infra error and outcome contradict"
            )
        transcripts.append(t)

    if not transcripts:
        raise ValueError(f"There are no transcripts (<scenario>.<engine>.p<pass>.json) in '{directory}'.")
    return sorted(transcripts, key=lambda t: (t["scenarioId"], t["engine"], t["pass"]))


def tool_definitions() -> list[dict]:
    """The six tools as the evaluators take them: each a function with its name, description and schema."""
    return [
        {"type": "function", "name": tool["name"], "description": tool["description"], "parameters": tool["parameters"]}
        for tool in json.loads(TOOLS_FILE.read_text(encoding="utf-8"))
    ]


def tools_sha256() -> str:
    """The SHA-256 of tools.json's canonical JSON: the value the freeze records as toolsSha256."""
    return hashlib.sha256(TOOLS_FILE.read_bytes().rstrip(b"\n")).hexdigest()


def tools_drift(freeze_path: str | Path) -> str | None:
    """Why the tools given to the evaluators are not the frozen ones, or None when they are (or there is no freeze)."""
    path = Path(freeze_path)
    if not path.exists():
        return None
    frozen = json.loads(path.read_text(encoding="utf-8")).get("toolsSha256")
    if frozen != tools_sha256():
        return "The tools in fwa_eval/tools.json are not the tools the freeze records (toolsSha256 differs)."
    return None


def _call_id(record: dict) -> str:
    # The transcript keeps no provider call ids; the record's index is unique within the run.
    return f"call_{record['index']}"


def _tool_call(record: dict) -> dict:
    return {"type": "tool_call", "tool_call_id": _call_id(record), "name": record["tool"], "arguments": record["arguments"]}


def _tool_result(record: dict):
    if record.get("result") is not None:
        # The JSON text exactly as the model was given it.
        return record["result"]
    outcome = record["outcome"]
    return {
        "outcome": outcome,
        "approved": record.get("approved"),
        "note": _NO_RESULT_NOTE.format(why=_WHY.get(outcome, f"the call ended {outcome}")),
    }


def _response(t: dict) -> list[dict]:
    """The run as messages: per model answer, its tool calls, then their results; then the reply."""
    messages: list[dict] = []
    groups: list[list[dict]] = []
    for record in sorted(t["tools"], key=lambda r: r["index"]):
        answer = record.get("modelCallIndex")
        # Calls from one model answer share one assistant message; a call no answer is known for stands alone.
        if groups and answer is not None and groups[-1][0].get("modelCallIndex") == answer:
            groups[-1].append(record)
        else:
            groups.append([record])

    for group in groups:
        messages.append({"role": "assistant", "content": [_tool_call(r) for r in group]})
        for r in group:
            messages.append({
                "role": "tool", "tool_call_id": _call_id(r),
                "content": [{"type": "tool_result", "tool_result": _tool_result(r)}],
            })

    if t.get("finalReply") is not None:
        # A truncated run's partial text is kept as written; its outcome says it was cut off.
        messages.append({"role": "assistant", "content": [{"type": "text", "text": t["finalReply"]}]})
    return messages


def row(t: dict, definitions: list[dict] | None = None) -> dict:
    """One transcript as one dataset row."""
    tools = sorted(t["tools"], key=lambda r: r["index"])
    return {
        "scenario_id": t["scenarioId"],
        "engine": t["engine"],
        "pass": t["pass"],
        "outcome": t["outcome"],
        "cut_off_tool_calls": [r["index"] for r in tools if r.get("result") is None and r["outcome"] in _WHY],
        "query": t["task"],
        "response": _response(t),
        "tool_definitions": definitions if definitions is not None else tool_definitions(),
        "tool_calls": [_tool_call(r) for r in tools],
    }


def build(transcripts_dir: str | Path, out_path: str | Path | None = None) -> list[dict]:
    """The dataset: one row per transcript without an infrastructure error, written as JSONL.

    Written to `out_path`, or to dataset.jsonl beside the transcripts. Returns the rows.
    """
    definitions = tool_definitions()
    rows = [row(t, definitions) for t in load_transcripts(transcripts_dir) if not t["infraError"]]
    out = Path(out_path) if out_path is not None else Path(transcripts_dir) / DATASET_FILE
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="\n") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    return rows
