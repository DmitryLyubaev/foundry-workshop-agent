# fwa_eval: the evaluators and the pre-registered comparison

This package turns the runner's transcripts into the study's results. It does three things:

1. It scores the transcripts with Microsoft Foundry's built-in agent evaluators. These scores are
   descriptive.
2. It computes the pre-registered comparison, C1 = GPT − Claude in task success (spec §5.3).
3. It renders the Markdown report.

## Setup

Use Python 3.14. Run these commands from the repository root:

```powershell
python -m venv eval/.venv          # git-ignored
eval/.venv/Scripts/python -m pip install -r eval/requirements.txt
eval/.venv/Scripts/python -m pytest eval/tests -q
```

The tests run offline. They use recorded transcripts (`tests/fixtures/`, made up), temporary
freezes and a fake evals client. CI runs the same tests on `ubuntu-latest`.

## Commands

Each command takes a transcripts directory. Each `--study` run writes to its own directory, and
both engines' transcripts go to the same one.

```powershell
python -m fwa_eval dataset <dir>   # writes <dir>/dataset.jsonl
python -m fwa_eval eval    <dir>   # dataset, then the cloud evaluation, then <dir>/eval-scores.json
python -m fwa_eval analyse <dir>   # the comparison, as JSON
python -m fwa_eval report  <dir>   # writes <dir>/report.md and prints it
```

Run them from `eval/`, or put `eval/` on `PYTHONPATH`.

- **`--freeze`** defaults to `scenarios/freeze.json`.
- **`eval`** reads the project endpoint from `FWA_PROJECT_ENDPOINT`. The endpoint is never printed.
  - It takes the judge deployment from `--judge`, else `FWA_GPT_DEPLOYMENT`, else `gpt-5.6-luna`.
  - It refuses to run if `fwa_eval/tools.json` is not the set of tools the freeze hashed.

## The dataset

The dataset has one JSONL row per transcript. Runs marked `infraError` are left out. Each row has
these fields:

| Field | Holds |
|---|---|
| `query` | The task the model was given. |
| `response` | The run, as messages. For each model answer: an assistant message holding its `tool_call`s, then a `tool` message for each `tool_result`, exactly as the model received it. Last comes the final reply, as text. |
| `tool_definitions` | The six tools, from `fwa_eval/tools.json`. A .NET test (`EvalToolsFileTests`) holds that file equal to the canonical JSON the freeze hashes. |
| `tool_calls` | Every call the model made, in order. This includes calls that were refused, denied or over the budget. |
| `scenario_id`, `engine`, `pass`, `outcome`, `cut_off_tool_calls` | Fields that join each score back to its transcript. No evaluator reads them. |

**Calls cut off with no result.** The run can end while a call is running (`cancelled`), or the call
can fail (`error`). Either way, the call has no result. The dataset does not show this as a call
that did nothing:

- The call's tool result gives the outcome and the approval.
- It also carries a note that the app may still have carried the call out.

An approved destructive press is recorded `cancelled` before it is sent. So a near-zero time on that
record does not mean the press never happened. The end-state checks say whether it did.

## The evaluation

There is one cloud evaluation through the project's OpenAI client (azure-ai-projects 2.x):

1. `evals.create` sets it up, with three `azure_ai_evaluator` testing criteria and the judge
   deployment.
2. `evals.runs.create` sends the rows inline (`jsonl`, `file_content`).
3. The package polls until the run ends, then reads `evals.runs.output_items.list`.

| Evaluator | Status | Reads |
|---|---|---|
| `builtin.tool_call_accuracy` | | query, tool_definitions, tool_calls, response |
| `builtin.task_adherence` | preview | query, response, tool_definitions |
| `builtin.intent_resolution` | preview | query, response, tool_definitions |

**Keyless.** Sign-in goes through the Azure CLI (`AzureCliCredential`):

- Locally, it uses the owner's `az login`.
- In CI, it uses the `azure/login` OIDC sign-in of the CI identity.

No key exists.

**What is saved.** `eval-scores.json` keeps the per-row scores, the status and the counts. It keeps
no id, URL or endpoint. Service errors are redacted before they are printed.

## The comparison

C1 is computed in four steps:

1. Each engine's task success on a scenario is its mean over the scenario's passes. Runs with
   infrastructure errors are left out.
2. The two engines are paired by scenario.
3. C1 is the mean of the paired differences.
4. The bootstrap resamples the scenarios.

**The decision rule** comes from the freeze's `decisionRule`, not from this package:

- threshold 0.10
- seed 20261004
- 10,000 resamples
- 3 passes

A **difference** is declared only when both of these hold:

- |C1| ≥ threshold. A mean of exactly ±0.10 counts.
- The 95% interval excludes zero. An interval that touches zero does not.

Anything else is **inconclusive at n scenarios**.

**How the interval is computed:**

- Percentiles are taken by nearest rank.
- Every figure the rule reads is settled to 12 decimal places, so float residue cannot flip a
  verdict.

**Without a freeze,** the pre-registered values are used, and the report says the run is not a
frozen study run.

**Two more checks in the report:**

- If the transcripts' instruction or settings hashes differ from the freeze's, the report warns.
- If any scenario has fewer passes than the rule asks for, the report calls the run a smoke run.

## Prices

`fwa_eval/prices.py` fixes the prices, with their source and the date they were read (2026-10-04):

| Model | Input, per 1M tokens | Output, per 1M tokens |
|---|---:|---:|
| `gpt-5.6-luna` | $0.20 | $1.20 |
| Claude Haiku 4.5 | $1 | $5 |

Costs come from the tokens the transcripts recorded. Any other model's cost is reported as
unknown.

## What the report never prints

Review Focus 4 forbids identifiers in the report. A test scans for them. The report never prints:

- a GUID, endpoint, URL, key, token or email
- a deployment or resource name the owner chose. Only public model names in the price list are
  printed.
- free text from a transcript

The only hash it prints is the freeze's 12-character short hash.
