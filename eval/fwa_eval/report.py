"""The study's Markdown report: the figures per engine, the evaluator scores, the verdict and the limits.

What it may say is what spec §10 allows: the verdict worded as rendered, with the date and n; the
per-engine figures and evaluator scores as descriptive, preview evaluators labelled; and the limits
§10 and §12 name. It prints no identifier: no deployment or resource name the owner chose (a model
is named only when it is a public model in the price list), no id, endpoint, URL or hash but the
freeze's 12-character short hash, and no free text from a transcript (messages, errors, replies).
"""

from __future__ import annotations

import datetime as dt
import math
import re
from pathlib import Path

from . import prices
from .dataset import load_transcripts
from .run_eval import EVALUATORS

_ENGINE_LABELS = {"gpt": "GPT", "claude": "Claude", "fake": "Fake"}
_ENGINE_ORDER = ("gpt", "claude", "fake")

_EVALUATOR_LABELS = {
    "tool_call_accuracy": "Tool call accuracy",
    "task_adherence": "Task adherence",
    "intent_resolution": "Intent resolution",
}
_PREVIEW = {name for name, _, preview, _ in EVALUATORS if preview}

_OUTCOME_ORDER = (
    "completed", "tool_limit", "time_limit", "content_filtered", "truncated", "throttled", "engine_error",
    "service_error", "infra_error",
)
_INFRA_OUTCOMES = {"service_error", "infra_error"}
_KNOWN_ENGINE = re.compile(r"^[a-z][a-z0-9-]{0,15}$")
_DATE = re.compile(r"^\d{4}-\d{2}-\d{2}")


def _engine_label(engine: str) -> str:
    if engine in _ENGINE_LABELS:
        return _ENGINE_LABELS[engine]
    # The runner's engines are short lower-case names; anything else is not printed as given.
    return engine if _KNOWN_ENGINE.match(engine) else "(another engine)"


def _engines(names) -> list[str]:
    names = set(names)
    return [e for e in _ENGINE_ORDER if e in names] + sorted(names - set(_ENGINE_ORDER))


def _mean(values: list[float]) -> float | None:
    return math.fsum(values) / len(values) if values else None


def _pct(numerator: int, denominator: int) -> str:
    return f"{100 * numerator / denominator:.1f}%" if denominator else "–"


def _money(value: float | None) -> str:
    return "unknown" if value is None else f"${value:.4f}"


def _signed(value: float) -> str:
    return f"{value:+.3f}"


def _plural(n: int, one: str, many: str) -> str:
    return f"{n} {one if n == 1 else many}"


def _models(runs: list[dict]) -> str:
    models = sorted({t["model"] for t in runs})
    if all(m in prices.PRICES for m in models):
        return ", ".join(f"`{m}`" for m in models)
    return "(not in the price list)"


def _tokens(t: dict) -> tuple[int, int]:
    return sum(c["inputTokens"] for c in t["calls"]), sum(c["outputTokens"] for c in t["calls"])


def _cost(t: dict) -> float | None:
    return prices.cost(t["model"], *_tokens(t))


def _dates(transcripts: list[dict]) -> str:
    days = sorted({t["startedAt"][:10] for t in transcripts if _DATE.match(str(t["startedAt"]))})
    if not days:
        return "unknown"
    return days[0] if len(days) == 1 else f"{days[0]} to {days[-1]}"


def _header(analysis: dict, transcripts: list[dict], rendered_on: dt.date) -> list[str]:
    rule = analysis["rule"]
    lines = [
        "# Foundry workshop agent: study report",
        "",
        f"- **Date:** {_dates(transcripts)} (runs, UTC); rendered {rendered_on.isoformat()}",
        f"- **Scenarios compared (n):** {analysis['n']}",
    ]
    freeze = analysis["freeze"]
    if freeze is None:
        lines.append(
            "- **Freeze:** none: these transcripts are not a frozen study run, and the rule below is the "
            "pre-registered one as written in the code."
        )
    else:
        lines.append(f"- **Freeze:** `{freeze['short_hash']}`, frozen on {freeze['frozen_on']}")
        if not freeze["matches_transcripts"]:
            lines.append(
                f"- **Warning:** these transcripts were not run under this freeze ({_mismatch_text(freeze)}), "
                "so the verdict below is not the study's."
            )
    runs = (
        f"- **Runs:** {analysis['runs_made']} of the {analysis['runs_expected']} the rule asks for "
        f"({_plural(analysis['scenarios_expected'], 'scenario', 'scenarios')} × {_plural(rule['passes'], 'pass', 'passes')} × 2 engines), "
        "counting runs dropped for an infrastructure error"
    )
    lines.append(runs + ("." if analysis["complete"] else ": a smoke run, not the study."))
    return lines


_MISMATCH_TEXT = {
    "instructions": "their instructions differ from it",
    "settings": "their settings differ from it",
    "tools": "the tools differ from it",
    "scenarios": "they hold scenarios it does not list",
}


def _mismatch_text(freeze: dict) -> str:
    return "; ".join(_MISMATCH_TEXT.get(m, "they differ from it") for m in freeze.get("mismatches", [])) or "they differ from it"


def _success(engines: list[str], by_engine: dict[str, list[dict]]) -> list[str]:
    lines = [
        "## Task success",
        "",
        "Task success, the primary measure, is decided by the database: every end-state check passed, no gate "
        "violation, the run completed, and the infrastructure held. Runs that failed for the infrastructure are "
        "dropped (spec §5.4).",
        "",
        "| Engine | Runs | Infrastructure errors (dropped) | Kept | Successes | Task success |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for e in engines:
        runs = by_engine[e]
        kept = [t for t in runs if not t["infraError"]]
        successes = sum(1 for t in kept if t["success"])
        lines.append(
            f"| {_engine_label(e)} ({_models(runs)}) | {len(runs)} | {len(runs) - len(kept)} | {len(kept)} | "
            f"{successes} | {_pct(successes, len(kept))} |"
        )
    return lines


def _costs(engines: list[str], by_engine: dict[str, list[dict]]) -> list[str]:
    lines = [
        "## Tokens, cost and time per task",
        "",
        "| Engine | Input tokens per task | Output tokens per task | Cost per task | Cost of the scored runs | Time per task |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for e in engines:
        kept = [t for t in by_engine[e] if not t["infraError"]]
        tokens = [_tokens(t) for t in kept]
        costs = [_cost(t) for t in kept]
        per_task = None if None in costs else _mean(costs)
        total = None if None in costs else math.fsum(costs)
        input_mean, output_mean = _mean([i for i, _ in tokens]), _mean([o for _, o in tokens])
        time_mean = _mean([t["ms"] for t in kept])
        lines.append(
            f"| {_engine_label(e)} | {'–' if input_mean is None else f'{input_mean:,.0f}'} | "
            f"{'–' if output_mean is None else f'{output_mean:,.0f}'} | {_money(per_task) if kept else '–'} | "
            f"{_money(total) if kept else '–'} | {'–' if time_mean is None else f'{time_mean / 1000:.1f} s'} |"
        )
    lines += [
        "",
        "Over the scored runs: those without an infrastructure error. Time: the whole run, from its fresh "
        "database to its checks.",
        "",
        "Runs dropped for an infrastructure error are not scored, but any model calls they made were billed:",
        "",
        "| Engine | Infrastructure-error runs | Input tokens | Output tokens | Cost |",
        "|---|---:|---:|---:|---:|",
    ]
    for e in engines:
        infra = [t for t in by_engine[e] if t["infraError"]]
        tokens = [_tokens(t) for t in infra]
        costs = [_cost(t) for t in infra]
        lines.append(
            f"| {_engine_label(e)} | {len(infra)} | {sum(i for i, _ in tokens):,} | {sum(o for _, o in tokens):,} | "
            f"{_money(None if None in costs else math.fsum(costs))} |"
        )
    lines += [
        "",
        f"Costs are the recorded tokens at the prices read on {prices.READ_ON} ({prices.SOURCE}), per 1M tokens:",
        "",
        "| Model | Input | Output |",
        "|---|---:|---:|",
    ]
    for model, price in prices.PRICES.items():
        lines.append(f"| `{model}` | ${price.input_per_million:.2f} | ${price.output_per_million:.2f} |")
    return lines


def _tools(engines: list[str], by_engine: dict[str, list[dict]]) -> list[str]:
    lines = [
        "## Tool calls, gate violations and outcomes",
        "",
        "| Engine | Tool calls per task | Gate violations |",
        "|---|---:|---:|",
    ]
    for e in engines:
        runs = by_engine[e]
        kept = [t for t in runs if not t["infraError"]]
        calls = _mean([len(t["tools"]) for t in kept])
        violations = sum(t["gateViolations"] for t in runs)
        lines.append(f"| {_engine_label(e)} | {'–' if calls is None else f'{calls:.2f}'} | {violations} |")

    outcomes = {t["outcome"] for runs in by_engine.values() for t in runs}
    ordered = [o for o in _OUTCOME_ORDER if o in outcomes] + sorted(outcomes - set(_OUTCOME_ORDER))
    lines += [
        "",
        "Outcomes by kind, over every run (gate violations are expected to be 0):",
        "",
        "| Outcome | " + " | ".join(_engine_label(e) for e in engines) + " |",
        "|---|" + "---:|" * len(engines),
    ]
    for outcome in ordered:
        label = outcome if re.match(r"^[a-z_]{1,32}$", outcome) else "(other)"
        if outcome in _INFRA_OUTCOMES:
            label += " (infrastructure)"
        counts = [sum(1 for t in by_engine[e] if t["outcome"] == outcome) for e in engines]
        lines.append(f"| {label} | " + " | ".join(str(c) for c in counts) + " |")

    cut = [r for runs in by_engine.values() for t in runs for r in t["tools"]
           if r.get("result") is None and r["outcome"] in ("cancelled", "error")]
    presses = sum(1 for r in cut if r["tool"] == "press_button" and r.get("approved") is True)
    lines.append("")
    if cut:
        lines.append(
            f"{_plural(len(cut), 'tool call', 'tool calls')} ({_plural(presses, 'approved destructive press', 'approved destructive presses')}) "
            f"{'was' if len(cut) == 1 else 'were'} cut off by the run's end or a failure, with no result for the "
            "model. Such a call is recorded `cancelled` or `error` with a near-zero time, but the app may still "
            "have carried it out: the end-state checks, not the record, say whether it did."
        )
    else:
        lines.append("No tool call was cut off.")
    return lines


def _evaluators(eval_scores: dict | None) -> list[str]:
    lines = ["## Foundry evaluator scores", ""]
    if eval_scores is None:
        return lines + ["The evaluators were not run for these transcripts."]

    judge = eval_scores.get("judge")
    judge_text = f"`{judge}`" if judge in prices.PRICES else "a deployment not in the price list"
    preview = _PREVIEW | {e.get("name") for e in eval_scores.get("evaluators", []) if e.get("preview")}
    rows = eval_scores.get("rows", [])
    engines = _engines(r["engine"] for r in rows)
    names = [name for name, *_ in EVALUATORS]

    lines += [
        f"Judge: {judge_text}. The scores are descriptive: task success, decided by the database, is the "
        "measure. Preview evaluators are labelled (preview). Each mean is over the rows the judge scored, "
        "shown as scored of rows; the pass rate is over the rows with a pass or fail.",
        "",
        "| Evaluator | " + " | ".join(f"{_engine_label(e)} mean score | {_engine_label(e)} pass rate" for e in engines) + " |",
        "|---|" + "---:|---:|" * len(engines),
    ]
    for name in names:
        label = _EVALUATOR_LABELS[name] + (" (preview)" if name in preview else "")
        cells = []
        for e in engines:
            own = [r["scores"].get(name, {}) for r in rows if r["engine"] == e]
            scored = [s["score"] for s in own if isinstance(s.get("score"), (int, float)) and not isinstance(s.get("score"), bool)]
            judged = [s["passed"] for s in own if isinstance(s.get("passed"), bool)]
            mean = _mean([float(v) for v in scored])
            cells.append(f"{'–' if mean is None else f'{mean:.2f}'} ({len(scored)} of {len(own)})")
            cells.append(_pct(sum(judged), len(judged)))
        lines.append(f"| {label} | " + " | ".join(cells) + " |")
    return lines


def _comparison(analysis: dict) -> list[str]:
    rule = analysis["rule"]
    x, y = _engine_label(analysis["x"]), _engine_label(analysis["y"])
    lines = [
        f"## The pre-registered comparison (C1 = {x} − {y})",
        "",
        f"The rule{' from the freeze' if analysis['freeze'] else ''}: a difference is declared only when the mean "
        f"paired difference in task success is at least the threshold either way and its 95% bootstrap interval "
        f"excludes zero; an interval touching zero does not. Each scenario is its mean over its passes. "
        f"Rule: threshold {rule['threshold']:.2f}, seed {rule['seed']}, {rule['resamples']:,} resamples, "
        f"{rule['passes']} passes; percentiles by nearest rank, settled to 12 decimal places.",
        "",
    ]
    n = analysis["n"]
    if n == 0:
        lines.append("No scenario has a run of both engines without an infrastructure error.")
    else:
        lines.append(
            f"C1 = {x} − {y} = {_signed(analysis['mean'])} (95% interval {_signed(analysis['ci_low'])} to "
            f"{_signed(analysis['ci_high'])}), over {_plural(n, 'scenario', 'scenarios')}."
        )
    lines.append("")
    freeze = analysis["freeze"]
    # Said on the verdict itself, not only in the header: the line is the one that gets quoted.
    caveat = " (transcripts do not match the freeze)" if freeze is not None and not freeze["matches_transcripts"] else ""
    if analysis["verdict"] == "difference":
        favoured = _engine_label(analysis["favours"])
        lines.append(f"**Verdict: difference{caveat}.** {favoured}'s task success is higher, over {_plural(n, 'scenario', 'scenarios')}.")
    else:
        lines.append(f"**Verdict: {analysis['verdict']}{caveat}.**")

    infra = [r for r in analysis.get("infra_runs", []) if re.match(r"^[\w-]{1,32}$", r["scenario"])]
    lines.append("")
    if infra:
        listed = ", ".join(f"{_engine_label(r['engine'])} {r['scenario']} pass {r['pass']}" for r in infra)
        lines.append(
            f"Dropped from the pairs for an infrastructure error (spec §5.4): "
            f"{_plural(len(analysis['infra_runs']), 'run', 'runs')}: {listed}."
        )
    else:
        lines.append("No run was dropped for an infrastructure error.")

    if analysis["per_scenario"]:
        lines += ["", f"| Scenario | {x} | {y} | C1 |", "|---|---:|---:|---:|"]
        for row in analysis["per_scenario"]:
            scenario = row["scenario"] if re.match(r"^[\w-]{1,32}$", row["scenario"]) else "(other)"
            lines.append(
                f"| {scenario} | {row['x_mean']:.3f} ({row['x_passes']}) | {row['y_mean']:.3f} ({row['y_passes']}) | "
                f"{_signed(row['delta'])} |"
            )
        lines.append("")
        lines.append("Each engine's mean task success on the scenario, with its kept passes in brackets.")
    if analysis["unpaired"]:
        left_out = ", ".join(u["scenario"] for u in analysis["unpaired"] if re.match(r"^[\w-]{1,32}$", u["scenario"]))
        lines += ["", f"Left out, for want of a run without an infrastructure error on both sides: {left_out}."]
    return lines


def _limits(analysis: dict) -> list[str]:
    n = analysis["n"]
    return [
        "## Limits",
        "",
        "- No claim that either model is better is made without a declared difference; an inconclusive verdict "
        "is reported as inconclusive.",
        "- Claude's tool loop runs in the client, not inside Foundry Agent Service: Claude Haiku 4.5 is deployed in "
        "the same Foundry resource and called through the Messages API, which a Foundry prompt agent cannot drive. "
        "GPT runs as a Foundry prompt agent.",
        f"- Nothing here speaks for other apps, other tasks or production use: {_plural(n, 'scenario', 'scenarios')} "
        "on one made-up workshop app.",
        "- The evaluator scores are descriptive. Task adherence and intent resolution are preview evaluators, and "
        "may change or disappear. The judge is the same `gpt-5.6-luna` the GPT engine runs on.",
        "- The scenarios were written by Claude, one of the two vendors compared: a possible bias, stated rather "
        "than removed. Task success is decided by the database, not by a judge.",
        f"- Costs are the recorded tokens at the prices of {prices.READ_ON}; they leave out the judge's tokens and "
        "Application Insights.",
    ]


def render(analysis: dict, eval_scores: dict | None, transcripts_dir: str | Path, *, rendered_on: dt.date | None = None) -> str:
    """The report as Markdown. `eval_scores` is run_eval.run's result, or None when the evaluators were not run."""
    transcripts = load_transcripts(transcripts_dir)
    by_engine: dict[str, list[dict]] = {}
    for t in transcripts:
        by_engine.setdefault(t["engine"], []).append(t)
    engines = _engines(by_engine)

    sections = [
        _header(analysis, transcripts, rendered_on or dt.datetime.now(dt.UTC).date()),
        _success(engines, by_engine),
        _costs(engines, by_engine),
        _tools(engines, by_engine),
        _evaluators(eval_scores),
        _comparison(analysis),
        _limits(analysis),
    ]
    return "\n\n".join("\n".join(section) for section in sections) + "\n"
