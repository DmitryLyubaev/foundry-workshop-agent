# The scenarios

The 20 benchmark scenarios, one `<id>.json` each. This directory is what the study runs.

## The freeze

`freeze.json`, once it exists, records what the study holds still:

- a SHA-256 for each scenario file, over its bytes (the repository pins them to LF);
- `instructionsSha256`: the agent's instructions (`AgentInstructions.Text`);
- `settingsSha256`: the model settings every engine sends and the limits every run is held to
  (`AgentSettings`: 4,096 output tokens a call, the model's own temperature, 25 tool calls,
  5 minutes and 60 s of throttling waits);
- `toolsSha256`: the six tools' names, descriptions and JSON schemas (`ToolSchemas.Sha256`);
- `decisionRule`: the threshold 0.10, seed 20261004, 10,000 resamples and 3 passes;
- `frozenOn`: the date.

```
dotnet run --project src/Workshop.Agent -- scenarios freeze scenarios
```

writes it, after checking that the scenarios load, and will not overwrite one: delete the file to
freeze again. `freeze.json` is not a scenario; the loader skips it.

`run --study` verifies the freeze before anything else (before any Foundry setting is read, any
agent made or any model called) and refuses with the first thing that differs: a scenario edited,
added or removed, then the instructions, the settings, the tools, the decision rule. It runs every
scenario (it refuses `--only`), exactly 3 passes, and puts the freeze's short hash (12 hex
characters of the SHA-256 of `freeze.json`) in the output directory's name. `run --frozen` makes
the same check with any scenario and any number of passes, which the live CI workflow uses. The runner checks again before every run. A run with neither flag
ignores the freeze.

Any edit to a scenario after the freeze changes what the study measures: re-freeze and say so.
