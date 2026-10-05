"""The dataset the evaluators score: one row per transcript, infrastructure errors left out."""

from __future__ import annotations

import hashlib
import json

import pytest

from conftest import RECORDED, transcript, write, write_freeze
from fwa_eval import dataset

TOOL_NAMES = ["describe_screen", "list_screens", "open_screen", "press_button", "select_row", "set_field"]


@pytest.fixture
def rows(tmp_path):
    return dataset.build(RECORDED, out_path=tmp_path / "dataset.jsonl")


def _row(rows, scenario, engine):
    (row,) = [r for r in rows if (r["scenario_id"], r["engine"]) == (scenario, engine)]
    return row


def test_one_row_per_transcript_with_infra_errors_left_out(rows):
    # Six recorded transcripts; s02.gpt (the app failed) and s03.gpt (the model's service failed)
    # are infrastructure errors. notes.json is not a transcript and is ignored.
    assert [(r["scenario_id"], r["engine"], r["pass"]) for r in rows] == [
        ("s01", "claude", 1), ("s01", "gpt", 1), ("s02", "claude", 1), ("s03", "claude", 1),
    ]


def test_each_row_has_the_evaluators_fields(rows):
    for row in rows:
        assert set(row) == {
            "scenario_id", "engine", "pass", "outcome", "cut_off_tool_calls",
            "query", "response", "tool_definitions", "tool_calls",
        }
        assert row["query"] == f"Made-up task for {row['scenario_id']}."
        assert isinstance(row["response"], list)


def test_the_tool_definitions_are_the_six_tools(rows):
    for row in rows:
        definitions = row["tool_definitions"]
        assert [d["name"] for d in definitions] == TOOL_NAMES
        for d in definitions:
            assert d["type"] == "function"
            assert d["description"]
            assert d["parameters"]["type"] == "object"
    press = next(d for d in rows[0]["tool_definitions"] if d["name"] == "press_button")
    assert press["parameters"]["required"] == ["button"]


def test_the_response_is_the_conversation_as_the_agent_evaluators_read_it(rows):
    row = _row(rows, "s01", "gpt")

    assert row["response"] == [
        {"role": "assistant", "content": [
            {"type": "tool_call", "tool_call_id": "call_1", "name": "describe_screen", "arguments": {}},
        ]},
        {"role": "tool", "tool_call_id": "call_1", "content": [
            {"type": "tool_result", "tool_result": json.loads(RECORDED.joinpath("s01.gpt.p1.json").read_text(encoding="utf-8"))["tools"][0]["result"]},
        ]},
        {"role": "assistant", "content": [
            {"type": "tool_call", "tool_call_id": "call_2", "name": "set_field", "arguments": {"field": "search", "value": "Lee"}},
        ]},
        {"role": "tool", "tool_call_id": "call_2", "content": [
            {"type": "tool_result", "tool_result": '{"outcome":"ok","message":null,"screen":{"id":"jobs","title":"Jobs","fields":[],"buttons":[],"lists":[]}}'},
        ]},
        {"role": "assistant", "content": [
            {"type": "text", "text": "The Lee family's printer job, J-2001, is diagnosing."},
        ]},
    ]
    assert row["tool_calls"] == [
        {"type": "tool_call", "tool_call_id": "call_1", "name": "describe_screen", "arguments": {}},
        {"type": "tool_call", "tool_call_id": "call_2", "name": "set_field", "arguments": {"field": "search", "value": "Lee"}},
    ]
    assert row["outcome"] == "completed"
    assert row["cut_off_tool_calls"] == []


def test_tool_calls_from_one_model_answer_share_one_assistant_message(rows):
    row = _row(rows, "s01", "claude")

    roles = [m["role"] for m in row["response"]]
    assert roles == ["assistant", "tool", "tool", "assistant"]
    assert [c["name"] for c in row["response"][0]["content"]] == ["open_screen", "set_field"]
    # A refused call is still a call the model made, and its result is what the model saw.
    assert "not_found" in row["response"][2]["content"][0]["tool_result"]


def test_a_cut_off_press_is_not_presented_as_doing_nothing(rows):
    """The run ended while an approved destructive press was running: the record says cancelled,
    with no result and near-zero time, though the app may have done it (plan 3, carried to Task 6)."""
    row = _row(rows, "s02", "claude")

    assert row["outcome"] == "time_limit"
    assert row["cut_off_tool_calls"] == [1]
    (call,) = row["tool_calls"]
    assert call["name"] == "press_button"
    result = row["response"][1]["content"][0]["tool_result"]
    assert result["outcome"] == "cancelled"
    assert result["approved"] is True
    assert "may have" in result["note"]
    assert "end-state" in result["note"]
    # No final reply was given, and none is made up.
    assert [m["role"] for m in row["response"]] == ["assistant", "tool"]


def test_a_truncated_reply_is_kept_as_written(rows):
    row = _row(rows, "s03", "claude")

    assert row["outcome"] == "truncated"
    assert row["response"][-1] == {"role": "assistant", "content": [{"type": "text", "text": "I did not cancel J-2002 because"}]}


def test_the_jsonl_file_holds_the_rows(tmp_path):
    out = tmp_path / "dataset.jsonl"
    rows = dataset.build(RECORDED, out_path=out)

    lines = out.read_text(encoding="utf-8").splitlines()
    assert [json.loads(line) for line in lines] == rows


def test_build_writes_beside_the_transcripts_by_default(tmp_path):
    study = tmp_path / "study"
    write(study, transcript("s01", "gpt", 1))

    rows = dataset.build(study)

    assert (study / dataset.DATASET_FILE).read_text(encoding="utf-8") == json.dumps(rows[0], ensure_ascii=False) + "\n"
    # The dataset is not a transcript: building again reads the same one transcript.
    assert len(dataset.build(study)) == 1


def test_a_transcript_whose_name_disagrees_with_its_content_is_refused(tmp_path):
    study = tmp_path / "study"
    path = write(study, transcript("s01", "gpt", 1))
    path.rename(study / "s02.gpt.p1.json")

    with pytest.raises(ValueError, match="s02.gpt.p1.json"):
        dataset.build(study, out_path=tmp_path / "d.jsonl")


def test_an_empty_directory_is_refused(tmp_path):
    with pytest.raises(ValueError, match="no transcripts"):
        dataset.build(tmp_path, out_path=tmp_path / "d.jsonl")


def test_the_tools_file_is_checked_against_the_freeze(tmp_path):
    actual = hashlib.sha256(dataset.TOOLS_FILE.read_bytes().rstrip(b"\n")).hexdigest()
    assert dataset.tools_sha256() == actual

    matching = write_freeze(tmp_path / "a" / "freeze.json")
    data = json.loads(matching.read_text(encoding="utf-8"))
    data["toolsSha256"] = actual
    matching.write_text(json.dumps(data), encoding="utf-8")
    assert dataset.tools_drift(matching) is None

    drifted = write_freeze(tmp_path / "b" / "freeze.json")
    assert "tools" in dataset.tools_drift(drifted)

    assert dataset.tools_drift(tmp_path / "none" / "freeze.json") is None
