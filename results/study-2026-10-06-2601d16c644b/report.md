# Foundry workshop agent: study report

- **Date:** 2026-10-06 (runs, UTC); rendered 2026-10-06
- **Scenarios compared (n):** 20
- **Freeze:** `2601d16c644b`, frozen on 2026-10-05
- **Runs:** 120 of the 120 the rule asks for (20 scenarios × 3 passes × 2 engines), counting runs dropped for an infrastructure error.

## Task success

Task success, the primary measure, is decided by the database: every end-state check passed, no gate violation, the run completed, and the infrastructure held. Runs that failed for the infrastructure are dropped (spec §5.4).

| Engine | Runs | Infrastructure errors (dropped) | Kept | Successes | Task success |
|---|---:|---:|---:|---:|---:|
| GPT (`gpt-5.6-luna`) | 60 | 0 | 60 | 59 | 98.3% |
| Claude (`claude-haiku-4-5`) | 60 | 0 | 60 | 57 | 95.0% |

## Tokens, cost and time per task

| Engine | Input tokens per task | Output tokens per task | Cost per task | Cost of the scored runs | Time per task |
|---|---:|---:|---:|---:|---:|
| GPT | 21,149 | 351 | $0.0047 | $0.2791 | 18.3 s |
| Claude | 25,952 | 748 | $0.0297 | $1.7815 | 23.7 s |

Over the scored runs: those without an infrastructure error. Time: the whole run, from its fresh database to its checks.

Runs dropped for an infrastructure error are not scored, but any model calls they made were billed:

| Engine | Infrastructure-error runs | Input tokens | Output tokens | Cost |
|---|---:|---:|---:|---:|
| GPT | 0 | 0 | 0 | $0.0000 |
| Claude | 0 | 0 | 0 | $0.0000 |

Costs are the recorded tokens at the prices read on 2026-10-04 (gpt-5.6-luna from the Azure Retail Prices API (Global Standard, eastus2); Claude Haiku 4.5 at Anthropic's list price, billed through Azure Marketplace), per 1M tokens:

| Model | Input | Output |
|---|---:|---:|
| `gpt-5.6-luna` | $0.20 | $1.20 |
| `claude-haiku-4-5` | $1.00 | $5.00 |

## Tool calls, gate violations and outcomes

| Engine | Tool calls per task | Gate violations |
|---|---:|---:|
| GPT | 6.78 | 0 |
| Claude | 6.33 | 0 |

Outcomes by kind, over every run (gate violations are expected to be 0):

| Outcome | GPT | Claude |
|---|---:|---:|
| completed | 59 | 58 |
| tool_limit | 1 | 0 |
| throttled | 0 | 2 |

No tool call was cut off.

## Foundry evaluator scores

Judge: `gpt-5.6-luna`. The scores are descriptive: task success, decided by the database, is the measure. Preview evaluators are labelled (preview). Each mean is over the rows the judge scored, shown as scored of rows; the pass rate is over the rows with a pass or fail.

| Evaluator | GPT mean score | GPT pass rate | Claude mean score | Claude pass rate |
|---|---:|---:|---:|---:|
| Tool call accuracy | 3.75 (60 of 60) | 76.7% | 3.88 (60 of 60) | 73.3% |
| Task adherence (preview) | 0.98 (60 of 60) | 98.3% | 0.93 (60 of 60) | 93.3% |
| Intent resolution (preview) | 4.70 (60 of 60) | 98.3% | 4.67 (60 of 60) | 98.3% |

## The pre-registered comparison (C1 = GPT − Claude)

The rule from the freeze: a difference is declared only when the mean paired difference in task success is at least the threshold either way and its 95% bootstrap interval excludes zero; an interval touching zero does not. Each scenario is its mean over its passes. Rule: threshold 0.10, seed 20261004, 10,000 resamples, 3 passes; percentiles by nearest rank, settled to 12 decimal places.

C1 = GPT − Claude = +0.033 (95% interval +0.000 to +0.083), over 20 scenarios.

**Verdict: inconclusive at 20 scenarios.**

No run was dropped for an infrastructure error.

| Scenario | GPT | Claude | C1 |
|---|---:|---:|---:|
| s01 | 1.000 (3) | 1.000 (3) | +0.000 |
| s02 | 1.000 (3) | 1.000 (3) | +0.000 |
| s03 | 1.000 (3) | 1.000 (3) | +0.000 |
| s04 | 1.000 (3) | 1.000 (3) | +0.000 |
| s05 | 1.000 (3) | 1.000 (3) | +0.000 |
| s06 | 1.000 (3) | 1.000 (3) | +0.000 |
| s07 | 1.000 (3) | 1.000 (3) | +0.000 |
| s08 | 1.000 (3) | 1.000 (3) | +0.000 |
| s09 | 1.000 (3) | 1.000 (3) | +0.000 |
| s10 | 1.000 (3) | 1.000 (3) | +0.000 |
| s11 | 1.000 (3) | 1.000 (3) | +0.000 |
| s12 | 1.000 (3) | 1.000 (3) | +0.000 |
| s13 | 1.000 (3) | 0.667 (3) | +0.333 |
| s14 | 0.667 (3) | 0.333 (3) | +0.333 |
| s15 | 1.000 (3) | 1.000 (3) | +0.000 |
| s16 | 1.000 (3) | 1.000 (3) | +0.000 |
| s17 | 1.000 (3) | 1.000 (3) | +0.000 |
| s18 | 1.000 (3) | 1.000 (3) | +0.000 |
| s19 | 1.000 (3) | 1.000 (3) | +0.000 |
| s20 | 1.000 (3) | 1.000 (3) | +0.000 |

Each engine's mean task success on the scenario, with its kept passes in brackets.

## Limits

- No claim that either model is better is made without a declared difference; an inconclusive verdict is reported as inconclusive.
- Claude's tool loop runs in the client, not inside Foundry Agent Service: Claude Haiku 4.5 is deployed in the same Foundry resource and called through the Messages API, which a Foundry prompt agent cannot drive. GPT runs as a Foundry prompt agent.
- Nothing here speaks for other apps, other tasks or production use: 20 scenarios on one made-up workshop app.
- The evaluator scores are descriptive. Task adherence and intent resolution are preview evaluators, and may change or disappear. The judge is the same `gpt-5.6-luna` the GPT engine runs on.
- The scenarios were written by Claude, one of the two vendors compared: a possible bias, stated rather than removed. Task success is decided by the database, not by a judge.
- Costs are the recorded tokens at the prices of 2026-10-04; they leave out the judge's tokens and Application Insights.
- Time per task is the whole run, and includes any waits for throttling (at most 60 s a run); both deployments are given the same capacity.
