"""The command line: python -m fwa_eval <command> <transcripts_dir> [options].

  dataset  <dir> [--out PATH]                     write the evaluators' dataset (default <dir>/dataset.jsonl)
  eval     <dir> [--judge NAME] [--freeze PATH]   build the dataset and score it with Foundry's evaluators,
                                                  writing <dir>/eval-scores.json; the project endpoint is
                                                  read from FWA_PROJECT_ENDPOINT, the judge deployment from
                                                  --judge, FWA_GPT_DEPLOYMENT or gpt-5.6-luna; signed in
                                                  through the Azure CLI, keyless
  analyse  <dir> [--freeze PATH] [--out PATH]     the pre-registered comparison, as JSON
  report   <dir> [--scores PATH] [--freeze PATH] [--out PATH]
                                                  the Markdown report (default <dir>/report.md), also printed

--freeze defaults to scenarios/freeze.json in this repository, and without a file there the report says
there was no freeze; a --freeze path that holds no file is refused. Exit codes: 0 done, 1 failed, 2 usage.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

from . import analysis, dataset, report, run_eval

SCORES_FILE = "eval-scores.json"
REPORT_FILE = "report.md"
ENDPOINT_VARIABLE = "FWA_PROJECT_ENDPOINT"
JUDGE_VARIABLE = "FWA_GPT_DEPLOYMENT"


class _Usage(Exception):
    pass


class _Parser(argparse.ArgumentParser):
    def error(self, message):
        raise _Usage(message)


def _parser() -> argparse.ArgumentParser:
    parser = _Parser(prog="python -m fwa_eval", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True, parser_class=_Parser)

    p = commands.add_parser("dataset")
    p.add_argument("dir", type=Path)
    p.add_argument("--out", type=Path)

    p = commands.add_parser("eval")
    p.add_argument("dir", type=Path)
    p.add_argument("--judge")
    p.add_argument("--freeze", type=Path)

    p = commands.add_parser("analyse")
    p.add_argument("dir", type=Path)
    p.add_argument("--freeze", type=Path)
    p.add_argument("--out", type=Path)

    p = commands.add_parser("report")
    p.add_argument("dir", type=Path)
    p.add_argument("--scores", type=Path)
    p.add_argument("--freeze", type=Path)
    p.add_argument("--out", type=Path)
    return parser


def _write_json(path: Path, value) -> None:
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")


def _eval(args, out, err) -> int:
    endpoint = os.environ.get(ENDPOINT_VARIABLE)
    if not endpoint:
        print(f"{ENDPOINT_VARIABLE} is not set: it names the Foundry project the evaluation runs in.", file=err)
        return 2
    drift = dataset.tools_drift(args.freeze or analysis.DEFAULT_FREEZE)
    if drift:
        print(drift, file=err)
        return 2
    judge = args.judge or os.environ.get(JUDGE_VARIABLE) or run_eval.JUDGE_DEPLOYMENT
    dataset_path = args.dir / dataset.DATASET_FILE
    rows = dataset.build(args.dir, out_path=dataset_path)
    try:
        scores = run_eval.run(endpoint, judge, dataset_path, run_eval.default_credential())
    except Exception as e:  # noqa: BLE001 - any SDK failure is reported, redacted, never with its trace
        # A service's error text can name the project, its host or a principal: none of it reaches a log.
        print(run_eval.redact(f"The evaluation failed: {type(e).__name__}: {e}"), file=err)
        return 1
    _write_json(args.dir / SCORES_FILE, scores)
    print(f"Scored {len(rows)} transcripts; the scores are in {SCORES_FILE}.", file=out)
    return 0


def main(argv: list[str] | None = None, out=None, err=None) -> int:
    out = out or sys.stdout
    err = err or sys.stderr
    try:
        args = _parser().parse_args(argv)
    except _Usage as e:
        print(f"{e}\n\n{__doc__}", file=err)
        return 2

    freeze = getattr(args, "freeze", None)
    if freeze is not None and not freeze.is_file():
        # A mistyped path must not pass for "no freeze" and report the study unfrozen.
        print(f"There is no freeze at '{freeze}': check the --freeze path, or leave the flag out to use "
              "scenarios/freeze.json (or report without a freeze when there is none).", file=err)
        return 2

    try:
        if args.command == "dataset":
            rows = dataset.build(args.dir, out_path=args.out)
            print(f"Wrote {len(rows)} rows.", file=out)
        elif args.command == "eval":
            return _eval(args, out, err)
        elif args.command == "analyse":
            result = analysis.compare(args.dir, freeze_path=args.freeze)
            if args.out:
                _write_json(args.out, result)
            print(json.dumps(result, indent=2, ensure_ascii=False), file=out)
        else:
            scores_path = args.scores or args.dir / SCORES_FILE
            scores = json.loads(scores_path.read_text(encoding="utf-8")) if scores_path.exists() else None
            text = report.render(analysis.compare(args.dir, freeze_path=args.freeze), scores, args.dir)
            (args.out or args.dir / REPORT_FILE).write_text(text, encoding="utf-8", newline="\n")
            print(text, file=out, end="")
    except (ValueError, OSError) as e:
        print(run_eval.redact(str(e)), file=err)
        return 1
    return 0


if __name__ == "__main__":
    # The report holds characters (−, §) a Windows console's code page cannot write.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    sys.exit(main())
