"""The pre-registered comparison (spec §5.3), over made-up transcripts and temp freezes.

C1 = GPT − Claude in task success, paired by scenario on each scenario's mean over its passes. A
difference needs |mean| ≥ the threshold AND a 95% bootstrap interval that excludes zero; anything
else is "inconclusive at n scenarios". The rule's numbers come from the freeze, never from here.
"""

from __future__ import annotations

import json
import math
from fractions import Fraction

import pytest

from conftest import transcript, write, write_freeze, write_study
from fwa_eval import analysis
from fwa_eval.analysis import _nearest_rank_index, bootstrap_ci, compare, verdict


def _pairs_study(directory, pairs):
    """Scenarios s01.. in order: (GPT's successes, Claude's successes) out of 3 passes each."""
    return write_study(directory, {
        f"s{i:02d}": ([1] * g + [0] * (3 - g), [1] * c + [0] * (3 - c))
        for i, (g, c) in enumerate(pairs, start=1)
    })


# --- the bootstrap ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("p", "n", "index"),
    [
        (Fraction(1, 40), 10_000, 249),
        (Fraction(39, 40), 10_000, 9749),
        (Fraction(1, 40), 40, 0),
        (Fraction(39, 40), 40, 38),
        (Fraction(1, 40), 1, 0),
        (Fraction(39, 40), 1, 0),
    ],
)
def test_percentiles_are_by_nearest_rank(p, n, index):
    """ceil(p × n) − 1, clamped to [0, n − 1]: 249 and 9749 for 10,000 resamples."""
    assert _nearest_rank_index(p, n) == index


class _ScriptedRng:
    """Hands out scripted resamples in order, in place of random.Random's draws."""

    def __init__(self, resamples):
        self._resamples = iter(resamples)

    def choices(self, population, k):
        drawn = next(self._resamples)
        assert len(drawn) == k
        assert all(d in population for d in drawn)
        return drawn


def test_bootstrap_against_a_hand_computed_case():
    """Three deltas, 40 scripted resamples. Their means, sorted, are −1, then 38 zeros, then +1.

    Nearest rank takes index ceil(40/40) − 1 = 0 for the 2.5th percentile and ceil(39) − 1 = 38
    for the 97.5th: (−1, 0). Interpolating would have given (−0.025, 0.025) instead.
    """
    deltas = [-1.0, 0.0, 1.0]
    resamples = [[-1.0, -1.0, -1.0], [1.0, 1.0, 1.0]] + [[0.0, 0.0, 0.0]] * 38

    assert bootstrap_ci(deltas, resamples=40, seed=0, rng=_ScriptedRng(resamples)) == (-1.0, 0.0)

    # And by hand again with a lopsided set: means 1/3 (20 times) and 2/3 (20 times).
    resamples = [[1.0, 0.0, 0.0]] * 20 + [[1.0, 1.0, 0.0]] * 20
    low, high = bootstrap_ci([0.0, 1.0, 0.5], resamples=40, seed=0, rng=_ScriptedRng(resamples))
    assert (low, high) == (round(1 / 3, 12), round(2 / 3, 12))


def test_bootstrap_is_seeded_and_settled():
    deltas = [1 / 3, 0.0, -1 / 3, 2 / 3, 0.0, 1 / 3]
    first = bootstrap_ci(deltas, resamples=10_000, seed=20261004)
    assert bootstrap_ci(deltas, resamples=10_000, seed=20261004) == first
    assert bootstrap_ci(deltas, resamples=10_000, seed=20261005) != first

    # Constant deltas give (d, d) exactly to 12 places, residue and all.
    for d in (0.1, 1 / 3, -2 / 3, 0.0):
        assert bootstrap_ci([d] * 7, resamples=1_000, seed=1) == (round(d, 12) + 0.0, round(d, 12) + 0.0)

    with pytest.raises(ValueError):
        bootstrap_ci([], resamples=10, seed=1)


# --- the verdict -----------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("mean", "ci", "expected"),
    [
        # A mean of exactly ±0.10 is a difference, when the interval excludes zero.
        (0.10, (0.01, 0.2), "difference"),
        (-0.10, (-0.2, -0.01), "difference"),
        (0.09, (0.01, 0.2), "inconclusive"),
        (-0.09, (-0.2, -0.01), "inconclusive"),
        # An interval touching zero does not exclude it.
        (0.10, (0.0, 0.2), "inconclusive"),
        (-0.25, (-0.5, 0.0), "inconclusive"),
        (0.25, (-0.05, 0.5), "inconclusive"),
    ],
)
def test_verdict_rule(mean, ci, expected):
    assert verdict(mean, ci, threshold=0.10) == expected


@pytest.mark.parametrize(
    ("pairs", "mean"),
    [
        # Thirty scenarios whose mean C1 is exactly +0.10, which floats sum to 0.09999999999999999.
        (
            [(3, 3), (0, 0), (2, 3), (1, 0), (2, 2), (2, 0), (0, 0), (1, 1), (1, 0), (3, 3),
             (3, 3), (1, 1), (2, 2), (2, 2), (2, 1), (0, 0), (3, 3), (3, 3), (2, 3), (0, 0),
             (1, 0), (1, 0), (1, 1), (2, 1), (2, 0), (3, 3), (1, 1), (2, 1), (0, 0), (3, 3)],
            0.1,
        ),
        # And exactly −0.10, which floats put at −0.09999999999999999.
        (
            [(2, 2), (3, 3), (3, 3), (3, 2), (1, 1), (0, 2), (0, 1), (2, 2), (1, 1), (0, 1),
             (3, 3), (3, 2), (0, 0), (1, 1), (1, 2), (3, 3), (1, 2), (0, 0), (0, 0), (1, 2),
             (1, 2), (1, 0), (0, 1), (0, 0), (0, 1), (0, 2), (3, 3), (2, 2), (2, 2), (0, 0)],
            -0.1,
        ),
    ],
    ids=["plus-0.10", "minus-0.10"],
)
def test_a_mean_of_exactly_the_threshold_is_a_difference(tmp_path, monkeypatch, pairs, mean):
    study = _pairs_study(tmp_path / "study", pairs)
    freeze = write_freeze(tmp_path / "freeze.json", scenarios=30)

    result = compare(study, freeze_path=freeze)

    assert result["n"] == 30
    assert result["mean"] == mean
    assert result["ci_low"] > 0 or result["ci_high"] < 0
    assert result["verdict"] == "difference"
    assert result["favours"] == ("gpt" if mean > 0 else "claude")

    # The case really is on the edge: left unsettled, float residue would flip the verdict.
    monkeypatch.setattr(analysis, "_settled", lambda v: v)
    unsettled = compare(study, freeze_path=freeze)
    assert abs(unsettled["mean"]) < 0.1
    assert unsettled["verdict"] == "inconclusive at 30 scenarios"


def test_an_interval_touching_zero_is_inconclusive(tmp_path):
    """Ten scenarios, three where GPT did one pass better: mean +0.10, and a resample that draws
    none of the three (0.7^10 ≈ 2.8% of them) puts the 2.5th percentile at exactly 0."""
    study = _pairs_study(tmp_path / "study", [(1, 0)] * 3 + [(2, 2)] * 7)

    result = compare(study, freeze_path=write_freeze(tmp_path / "freeze.json", scenarios=10))

    assert result["mean"] == 0.1
    assert result["ci_low"] == 0.0
    assert result["verdict"] == "inconclusive at 10 scenarios"
    assert result["favours"] is None


# --- pairing, passes and infra errors --------------------------------------------------------


def test_each_scenario_is_its_mean_over_passes_then_paired(tmp_path):
    study = write_study(tmp_path / "study", {
        "s01": ([1, 1, 0], [1, 0, 0]),   # 2/3 − 1/3 = +1/3
        "s02": ([0, 0, 0], [1, 1, 1]),   # 0 − 1 = −1
        "s03": ([1, 1, 1], [1, 1, 1]),   # 0
    })

    result = compare(study, freeze_path=write_freeze(tmp_path / "freeze.json", scenarios=3))

    assert result["n"] == 3
    by_id = {row["scenario"]: row for row in result["per_scenario"]}
    assert by_id["s01"]["x_mean"] == pytest.approx(2 / 3)
    assert by_id["s01"]["y_mean"] == pytest.approx(1 / 3)
    assert by_id["s01"]["delta"] == pytest.approx(1 / 3)
    assert by_id["s02"]["delta"] == -1.0
    assert result["mean"] == round((1 / 3 - 1 + 0) / 3, 12)
    assert result["x"] == "gpt" and result["y"] == "claude"
    assert (result["runs_made"], result["runs_expected"], result["complete"]) == (18, 18, True)


def test_infra_errors_are_dropped_from_the_pairs(tmp_path):
    study = tmp_path / "study"
    # s01: GPT's second pass failed for the infrastructure: its mean is over passes 1 and 3.
    for p, s in ((1, True), (3, False)):
        write(study, transcript("s01", "gpt", p, success=s))
    write(study, transcript("s01", "gpt", 2, success=False, outcome="infra_error", infra=True, final_reply=None))
    for p in (1, 2, 3):
        write(study, transcript("s01", "claude", p, success=False))
    # s02: every Claude pass hit a service error: no pair, so s02 is left out of n.
    for p in (1, 2, 3):
        write(study, transcript("s02", "gpt", p, success=True))
        write(study, transcript("s02", "claude", p, success=False, outcome="service_error", infra=True, final_reply=None))

    result = compare(study, freeze_path=write_freeze(tmp_path / "freeze.json", scenarios=2))

    assert result["n"] == 1
    (row,) = result["per_scenario"]
    assert (row["scenario"], row["x_passes"], row["y_passes"], row["x_mean"]) == ("s01", 2, 3, 0.5)
    assert result["unpaired"] == [{"scenario": "s02", "reason": "claude has no run without an infrastructure error"}]
    assert result["dropped_infra"] == {"gpt": 1, "claude": 3}
    # One scenario with a delta of +0.5 meets the rule as written; "complete" is what says how
    # little stands behind it, and the report says it too.
    assert (result["mean"], result["ci_low"], result["ci_high"], result["verdict"]) == (0.5, 0.5, 0.5, "difference")
    # Every run was made: dropping the infrastructure errors is the rule, not a shorter study.
    assert (result["runs_made"], result["runs_expected"], result["complete"]) == (12, 12, True)
    assert result["infra_runs"] == [
        {"scenario": "s01", "engine": "gpt", "pass": 2},
        {"scenario": "s02", "engine": "claude", "pass": 1},
        {"scenario": "s02", "engine": "claude", "pass": 2},
        {"scenario": "s02", "engine": "claude", "pass": 3},
    ]


def test_a_study_with_one_infra_error_is_still_the_study(tmp_path, freeze):
    """20 scenarios x 3 passes x 2 engines, one Claude pass a service error: all 120 runs were made."""
    study = write_study(tmp_path / "study", {f"s{i:02d}": ([1, 1, 0], [1, 0, 0]) for i in range(1, 21)})
    write(study, transcript("s07", "claude", 2, success=False, outcome="service_error", infra=True, final_reply=None))

    result = compare(study, freeze_path=freeze)

    assert (result["n"], result["runs_made"], result["runs_expected"], result["complete"]) == (20, 120, 120, True)
    assert result["infra_runs"] == [{"scenario": "s07", "engine": "claude", "pass": 2}]
    assert result["dropped_infra"] == {"gpt": 0, "claude": 1}


def test_a_run_short_of_the_frozen_scenarios_or_passes_is_incomplete(tmp_path, freeze):
    # Every frozen scenario, but one pass missing on one side.
    study = write_study(tmp_path / "a", {f"s{i:02d}": ([1, 1, 1], [1, 1, 1]) for i in range(1, 21)})
    (study / "s20.gpt.p3.json").unlink()
    short = compare(study, freeze_path=freeze)
    assert (short["runs_made"], short["complete"]) == (119, False)

    # Three passes each, but only 19 of the 20 frozen scenarios.
    study = write_study(tmp_path / "b", {f"s{i:02d}": ([1, 1, 1], [1, 1, 1]) for i in range(1, 20)})
    fewer = compare(study, freeze_path=freeze)
    assert (fewer["runs_made"], fewer["runs_expected"], fewer["complete"]) == (114, 120, False)


def test_no_pair_at_all_is_inconclusive_at_zero(tmp_path, freeze):
    study = tmp_path / "study"
    write(study, transcript("s01", "gpt", 1))

    result = compare(study, freeze_path=freeze)

    assert (result["n"], result["mean"], result["ci_low"], result["ci_high"]) == (0, None, None, None)
    assert result["verdict"] == "inconclusive at 0 scenarios"


def test_a_transcript_whose_success_breaks_the_rule_is_refused(tmp_path, freeze):
    study = tmp_path / "study"
    t = transcript("s01", "gpt", 1, outcome="truncated")
    t["success"] = True  # truncated is never success
    write(study, t)

    with pytest.raises(ValueError, match="s01.gpt.p1"):
        compare(study, freeze_path=freeze)


# --- the rule comes from the freeze ----------------------------------------------------------


def test_the_rule_is_read_from_the_freeze(tmp_path):
    study = _pairs_study(tmp_path / "study", [(3, 0)] * 4 + [(2, 1)] * 4)

    default = compare(study, freeze_path=write_freeze(tmp_path / "a" / "freeze.json"))
    assert default["rule"] == {"threshold": 0.1, "seed": 20261004, "resamples": 10_000, "passes": 3}
    assert default["verdict"] == "difference"

    # A higher threshold in the freeze, and the same data is inconclusive: no constant here decides.
    strict = compare(study, freeze_path=write_freeze(tmp_path / "b" / "freeze.json", threshold=0.9))
    assert strict["rule"]["threshold"] == 0.9
    assert strict["verdict"] == "inconclusive at 8 scenarios"

    # The seed and the resamples are the freeze's too.
    mixed = _pairs_study(tmp_path / "mixed", [(3, 0), (0, 1), (2, 1), (1, 1), (3, 2), (0, 2)])
    one = compare(mixed, freeze_path=write_freeze(tmp_path / "c" / "freeze.json"))
    other = compare(mixed, freeze_path=write_freeze(tmp_path / "d" / "freeze.json", seed=7, resamples=500))
    assert other["rule"]["seed"] == 7 and other["rule"]["resamples"] == 500
    assert (one["ci_low"], one["ci_high"]) != (other["ci_low"], other["ci_high"])
    assert (other["ci_low"], other["ci_high"]) == bootstrap_ci(
        [row["delta"] for row in other["per_scenario"]], resamples=500, seed=7,
    )

    # Passes: a freeze asking for 1 makes a one-pass run complete.
    smoke = _pairs_study(tmp_path / "smoke", [])
    write(smoke, transcript("s01", "gpt", 1))
    write(smoke, transcript("s01", "claude", 1))
    one_pass = write_freeze(tmp_path / "e" / "freeze.json", passes=1, scenarios=1)
    assert compare(smoke, freeze_path=one_pass)["complete"] is True
    assert compare(smoke, freeze_path=write_freeze(tmp_path / "f" / "freeze.json", scenarios=1))["complete"] is False


def test_the_freeze_is_named_by_its_short_hash(tmp_path, freeze):
    import hashlib

    study = _pairs_study(tmp_path / "study", [(3, 0)])
    result = compare(study, freeze_path=freeze)

    assert result["freeze"]["short_hash"] == hashlib.sha256(freeze.read_bytes()).hexdigest()[:12]
    assert result["freeze"]["frozen_on"] == "2026-10-06"
    assert result["freeze"]["matches_transcripts"] is True


@pytest.mark.parametrize(
    ("change", "reason"),
    [
        ({"instructions_sha": "9" * 64}, "instructions"),
        ({"settings_sha": "8" * 64}, "settings"),
        ({"tools_sha": "7" * 64}, "tools"),
        # The transcripts hold s01, which this freeze does not list.
        ({"scenarios": ["s02", "s03"]}, "scenarios"),
    ],
)
def test_transcripts_the_freeze_does_not_account_for_do_not_match_it(tmp_path, change, reason):
    study = _pairs_study(tmp_path / "study", [(3, 0)])
    other = write_freeze(tmp_path / "freeze.json", **change)

    frozen = compare(study, freeze_path=other)["freeze"]

    assert (frozen["matches_transcripts"], frozen["mismatches"]) == (False, [reason])


def test_without_a_freeze_the_pre_registered_rule_is_used_and_said(tmp_path, monkeypatch):
    study = _pairs_study(tmp_path / "study", [(3, 0)])
    monkeypatch.setattr(analysis, "DEFAULT_FREEZE", tmp_path / "missing" / "freeze.json")

    result = compare(study)

    assert result["freeze"] is None
    assert result["rule"] == {"threshold": 0.1, "seed": 20261004, "resamples": 10_000, "passes": 3}


def test_a_freeze_path_that_holds_no_file_is_refused(tmp_path):
    """A mistyped path must not pass for "no freeze": only leaving the path out means that."""
    study = _pairs_study(tmp_path / "study", [(3, 0)])

    with pytest.raises(ValueError, match="no freeze at"):
        compare(study, freeze_path=tmp_path / "mistyped" / "freez.json")


def test_a_broken_freeze_is_refused(tmp_path):
    study = _pairs_study(tmp_path / "study", [(3, 0)])
    broken = tmp_path / "freeze.json"
    broken.write_text(json.dumps({"frozenOn": "2026-10-06"}), encoding="utf-8")

    with pytest.raises(ValueError, match="decisionRule"):
        compare(study, freeze_path=broken)


def test_the_result_is_plain_json(tmp_path, freeze):
    study = _pairs_study(tmp_path / "study", [(3, 0), (1, 2)])
    result = compare(study, freeze_path=freeze)
    assert json.loads(json.dumps(result)) == result
    assert not any(isinstance(v, float) and math.isnan(v) for v in result.values())
