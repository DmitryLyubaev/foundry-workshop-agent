"""Builders for made-up transcripts and freezes, shaped as the runner writes them.

The shape is Workshop.Agent's Transcript record (src/Workshop.Agent/Runner/Transcript.cs) as
System.Text.Json writes it: camelCase, every field present, nulls written. Nothing here is real:
the tasks, replies and screens are invented, and no test calls Azure.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

FIXTURES = Path(__file__).parent / "fixtures"
RECORDED = FIXTURES / "transcripts"

MODELS = {"gpt": "gpt-5.6-luna", "claude": "claude-haiku-4-5", "fake": "fake"}

# The runner's hashes of the instructions and the settings: made-up values the temp freezes repeat.
INSTRUCTIONS_SHA = "1" * 64
SETTINGS_SHA = "2" * 64


def transcript(
    scenario: str,
    engine: str,
    pass_: int,
    *,
    success: bool = True,
    outcome: str = "completed",
    infra: bool = False,
    tools: list[dict] | None = None,
    calls: list[dict] | None = None,
    final_reply: str | None = "Done.",
    gate_violations: int = 0,
    ms: float = 12_000.0,
    instructions_sha: str = INSTRUCTIONS_SHA,
    settings_sha: str = SETTINGS_SHA,
) -> dict:
    """One transcript. `success` sets the end-state check; task success then follows the rule."""
    check_passed = success
    return {
        "scenarioId": scenario,
        "pass": pass_,
        "engine": engine,
        "model": MODELS[engine],
        "deployment": None if engine == "fake" else MODELS[engine],
        "agentVersion": "3" if engine == "gpt" else None,
        "task": f"Made-up task for {scenario}.",
        "instructionsSha256": instructions_sha,
        "settingsSha256": settings_sha,
        "outcome": outcome,
        "infraError": infra,
        "infraMessage": "The app did not start." if infra and outcome == "infra_error" else None,
        "calls": calls if calls is not None else [
            {"index": 1, "inputTokens": 1000, "outputTokens": 100, "ms": 800.0, "finishReason": "tool_calls"},
            {"index": 2, "inputTokens": 2000, "outputTokens": 50, "ms": 600.0, "finishReason": "stop"},
        ],
        "tools": tools if tools is not None else [
            {
                "index": 1, "modelCallIndex": 1, "tool": "describe_screen", "arguments": {},
                "outcome": "ok", "message": None, "screenId": "home", "ms": 40.0, "approved": None,
                "result": '{"id":"home","title":"Home","fields":[],"buttons":[],"lists":[]}',
            },
        ],
        "finalReply": final_reply,
        "gateViolations": gate_violations,
        "check": {"passed": check_passed, "failures": [] if check_passed else ["jobs: expected 16, got 15"]},
        "success": check_passed and gate_violations == 0 and not infra and outcome == "completed",
        "ms": ms,
        "startedAt": "2026-10-07T09:30:00+00:00",
        "error": None,
    }


def write(directory: Path, t: dict) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{t['scenarioId']}.{t['engine']}.p{t['pass']}.json"
    path.write_text(json.dumps(t, indent=2), encoding="utf-8")
    return path


def write_study(directory: Path, success: dict[str, tuple[list[int], list[int]]]) -> Path:
    """A study: for each scenario, GPT's and Claude's success over their passes (1 or 0 each)."""
    for scenario, (gpt, claude) in success.items():
        for engine, passes in (("gpt", gpt), ("claude", claude)):
            for i, s in enumerate(passes, start=1):
                write(directory, transcript(scenario, engine, i, success=bool(s)))
    return directory


def write_freeze(
    path: Path,
    *,
    threshold: float = 0.10,
    seed: int = 20261004,
    resamples: int = 10_000,
    passes: int = 3,
    instructions_sha: str = INSTRUCTIONS_SHA,
    settings_sha: str = SETTINGS_SHA,
) -> Path:
    """A freeze.json as Workshop.Agent writes it, with a made-up scenario hash."""
    path.parent.mkdir(parents=True, exist_ok=True)
    freeze = {
        "frozenOn": "2026-10-06",
        "scenarios": {"s01.json": "3" * 64},
        "instructionsSha256": instructions_sha,
        "settingsSha256": settings_sha,
        "toolsSha256": "4" * 64,
        "decisionRule": {"threshold": threshold, "seed": seed, "resamples": resamples, "passes": passes},
    }
    path.write_text(json.dumps(freeze, indent=2) + "\n", encoding="utf-8", newline="\n")
    return path


@pytest.fixture
def freeze(tmp_path: Path) -> Path:
    return write_freeze(tmp_path / "scenarios" / "freeze.json")
