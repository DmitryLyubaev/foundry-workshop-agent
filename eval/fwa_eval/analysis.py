"""The pre-registered comparison (spec §5.3): C1 = GPT − Claude in task success.

Each engine's task success on a scenario is its mean over the scenario's passes, infrastructure
errors left out (spec §5.4). The engines are paired by scenario, and C1 is the mean of the paired
differences. A difference is declared only when |C1| is at least the threshold AND the 95%
bootstrap interval excludes zero; an interval that touches zero does not. Anything else is
"inconclusive at n scenarios".

The rule's numbers (threshold, seed, resamples, passes) are read from the freeze's
`decisionRule`, which Workshop.Agent wrote before the study and checks before every study run.
The constants below are only what is used, and said, when there is no freeze at all.

The bootstrap resamples scenarios with replacement, `resamples` times, with random.Random(seed)
over the deltas in scenario-id order, and takes the 2.5th and 97.5th percentiles by nearest rank.
Every figure the rule reads is settled to 12 decimal places, so float residue cannot decide it.
"""

from __future__ import annotations

import hashlib
import json
import math
import random
from fractions import Fraction
from pathlib import Path

from .dataset import load_transcripts, tools_sha256

GPT = "gpt"
CLAUDE = "claude"

# The pre-registered rule, as Workshop.Agent's DecisionRule.PreRegistered has it: used only when
# there is no freeze, and then the analysis says so.
PRE_REGISTERED = {"threshold": 0.10, "seed": 20261004, "resamples": 10_000, "passes": 3}

DEFAULT_FREEZE = Path(__file__).resolve().parents[2] / "scenarios" / "freeze.json"

SETTLED_DECIMALS = 12

# 2.5th and 97.5th percentiles as exact fractions, so the rank is not left to float rounding.
_CI_LOWER = Fraction(1, 40)
_CI_UPPER = Fraction(39, 40)

_SHORT_HASH = 12


def settled(value: float) -> float:
    """`value` rounded to 12 places: what float arithmetic leaves on a mean of small fractions
    (a mean of exactly 0.10 coming out as 0.09999999999999999) goes, and nothing else does.
    Adding 0.0 turns a -0.0 into 0.0."""
    return round(value, SETTLED_DECIMALS) + 0.0


# Called through this name so a test can take the settling out and show it matters.
_settled = settled


def _nearest_rank_index(p: Fraction, n: int) -> int:
    """The index of fraction p of n sorted values: ceil(p × n) − 1, clamped to [0, n − 1]."""
    return min(n - 1, max(0, math.ceil(p * n) - 1))


def bootstrap_ci(deltas: list[float], *, resamples: int, seed: int, rng=None) -> tuple[float, float]:
    """The 95% bootstrap interval for the mean of the per-scenario deltas, settled.

    `rng` stands in for random.Random(seed) only in tests, to script the resamples.
    """
    if not deltas:
        raise ValueError("a bootstrap needs at least one delta")
    rng = rng if rng is not None else random.Random(seed)
    n = len(deltas)
    means = sorted(math.fsum(rng.choices(deltas, k=n)) / n for _ in range(resamples))
    return (
        _settled(means[_nearest_rank_index(_CI_LOWER, resamples)]),
        _settled(means[_nearest_rank_index(_CI_UPPER, resamples)]),
    )


def verdict(mean: float, ci: tuple[float, float], *, threshold: float) -> str:
    """"difference" when |mean| ≥ threshold and the interval excludes zero; else "inconclusive"."""
    if abs(mean) >= threshold and (ci[0] > 0 or ci[1] < 0):
        return "difference"
    return "inconclusive"


def _inconclusive(n: int) -> str:
    return f"inconclusive at {n} scenario{'' if n == 1 else 's'}"


def read_freeze(freeze_path: str | Path) -> dict | None:
    """The freeze's rule, short hash, date, scenario set and hashes; None when there is no freeze file."""
    path = Path(freeze_path)
    if not path.exists():
        return None
    raw = path.read_bytes()
    try:
        frozen = json.loads(raw)
        rule = frozen["decisionRule"]
        parsed = {
            "threshold": float(rule["threshold"]),
            "seed": int(rule["seed"]),
            "resamples": int(rule["resamples"]),
            "passes": int(rule["passes"]),
        }
        scenarios = sorted(name[: -len(".json")] for name in frozen["scenarios"] if name.endswith(".json"))
    except (ValueError, KeyError, TypeError) as e:
        raise ValueError(f"The freeze at '{path}' has no readable decisionRule or scenarios: {e!r}") from e
    if parsed["resamples"] < 1 or parsed["passes"] < 1:
        raise ValueError(f"The freeze at '{path}' has a decisionRule with no resamples or passes.")
    return {
        "rule": parsed,
        # As Workshop.Agent's Freeze.ShortHash: the start of the SHA-256 of the file's bytes.
        "short_hash": hashlib.sha256(raw).hexdigest()[:_SHORT_HASH],
        "frozen_on": frozen.get("frozenOn"),
        "scenarios": scenarios,
        "instructionsSha256": frozen.get("instructionsSha256"),
        "settingsSha256": frozen.get("settingsSha256"),
        "toolsSha256": frozen.get("toolsSha256"),
    }


def _mismatches(freeze: dict, transcripts: list[dict]) -> list[str]:
    """What about these transcripts the freeze does not account for, as short reasons.

    A transcript records the instructions' and the settings' hashes but not the tools'; the tools
    are checked as this package holds them (tools.json, which a .NET test keeps equal to the code's).
    A scenario the freeze does not list is a mismatch; a frozen scenario with no run is only an
    incomplete run.
    """
    reasons = []
    if any(t["instructionsSha256"] != freeze["instructionsSha256"] for t in transcripts):
        reasons.append("instructions")
    if any(t["settingsSha256"] != freeze["settingsSha256"] for t in transcripts):
        reasons.append("settings")
    if tools_sha256() != freeze["toolsSha256"]:
        reasons.append("tools")
    if {t["scenarioId"] for t in transcripts} - set(freeze["scenarios"]):
        reasons.append("scenarios")
    return reasons


def compare(
    transcripts_dir: str | Path, *, freeze_path: str | Path | None = None, x: str = GPT, y: str = CLAUDE,
) -> dict:
    """C1 = x − y over the transcripts, under the freeze's decision rule. Plain JSON out.

    `freeze_path` defaults to scenarios/freeze.json in this repository, and with no file there the
    pre-registered values are used and the result says there was no freeze. A path given that
    holds no file is refused: a mistyped path must not pass for "no freeze".
    """
    if freeze_path is not None and not Path(freeze_path).exists():
        raise ValueError(f"There is no freeze at '{freeze_path}'.")
    transcripts = load_transcripts(transcripts_dir)
    freeze = read_freeze(freeze_path if freeze_path is not None else DEFAULT_FREEZE)
    rule = freeze["rule"] if freeze else dict(PRE_REGISTERED)

    kept: dict[tuple[str, str], list[float]] = {}
    made: dict[tuple[str, str], set[int]] = {}
    scenarios: set[str] = set()
    dropped = {x: 0, y: 0}
    infra_runs = []
    for t in transcripts:
        if t["engine"] not in (x, y):
            continue
        scenarios.add(t["scenarioId"])
        made.setdefault((t["scenarioId"], t["engine"]), set()).add(t["pass"])
        if t["infraError"]:
            dropped[t["engine"]] += 1
            infra_runs.append({"scenario": t["scenarioId"], "engine": t["engine"], "pass": t["pass"]})
            continue
        kept.setdefault((t["scenarioId"], t["engine"]), []).append(1.0 if t["success"] else 0.0)

    per_scenario, unpaired = [], []
    for scenario in sorted(scenarios):
        missing = [e for e in (x, y) if (scenario, e) not in kept]
        if missing:
            unpaired.append({
                "scenario": scenario,
                "reason": " and ".join(missing) + f" ha{'s' if len(missing) == 1 else 've'} no run without an infrastructure error",
            })
            continue
        xs, ys = kept[(scenario, x)], kept[(scenario, y)]
        x_mean, y_mean = math.fsum(xs) / len(xs), math.fsum(ys) / len(ys)
        per_scenario.append({
            "scenario": scenario, "x_mean": x_mean, "y_mean": y_mean, "delta": x_mean - y_mean,
            "x_passes": len(xs), "y_passes": len(ys),
        })

    # The study is every frozen scenario × the rule's passes × both engines, counted as runs MADE:
    # a run dropped for an infrastructure error was made, and dropping it is the pre-registered
    # rule (spec §5.4), not a shorter study. Without a freeze, the scenarios seen stand in.
    expected_scenarios = freeze["scenarios"] if freeze else sorted(scenarios)
    runs_expected = len(expected_scenarios) * rule["passes"] * 2
    runs_made = sum(
        len({p for p in made.get((scenario, engine), set()) if p <= rule["passes"]})
        for scenario in expected_scenarios for engine in (x, y)
    )
    mismatches = _mismatches(freeze, transcripts) if freeze else []

    n = len(per_scenario)
    result = {
        "x": x,
        "y": y,
        "rule": rule,
        "freeze": None if freeze is None else {
            "short_hash": freeze["short_hash"],
            "frozen_on": freeze["frozen_on"],
            "matches_transcripts": not mismatches,
            "mismatches": mismatches,
        },
        "n": n,
        "scenarios_expected": len(expected_scenarios),
        "runs_expected": runs_expected,
        "runs_made": runs_made,
        "complete": runs_expected > 0 and runs_made == runs_expected,
        "mean": None,
        "ci_low": None,
        "ci_high": None,
        "verdict": _inconclusive(n),
        "favours": None,
        "per_scenario": per_scenario,
        "unpaired": unpaired,
        "dropped_infra": dropped,
        "infra_runs": infra_runs,
    }
    if n == 0:
        return result

    deltas = [row["delta"] for row in per_scenario]
    mean = _settled(math.fsum(deltas) / n)
    ci = bootstrap_ci(deltas, resamples=rule["resamples"], seed=rule["seed"])
    decided = verdict(mean, ci, threshold=rule["threshold"])
    result.update({
        "mean": mean,
        "ci_low": ci[0],
        "ci_high": ci[1],
        "verdict": "difference" if decided == "difference" else _inconclusive(n),
        "favours": (x if mean > 0 else y) if decided == "difference" else None,
    })
    return result
