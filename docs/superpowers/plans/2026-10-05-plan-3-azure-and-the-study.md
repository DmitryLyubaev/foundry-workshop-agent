# Plan 3: Azure and the study — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run the same agent on GPT, as a Foundry Agent Service prompt agent, and on Claude Haiku
4.5, deployed in Foundry, against the frozen scenarios, keyless throughout. Score both with
Foundry's evaluators, decide the comparison by the pre-registered rule, and publish dated,
measured results.

**Architecture:**
- **Tasks 1–7 build everything offline and free.** Their tests use HTTP fakes and Terraform mock
  providers, and nothing calls Azure. They cover:
  - the binding hardening carried over from plan 2
  - the two real engines behind plan 2's `IAgentEngine`
  - the freeze
  - the Terraform stacks
  - the trace exporter
  - the evaluation and the analysis
  - the live CI workflow
- **Task 8 is a runbook the owner gates step by step.** Each apply and each paid run waits for the
  owner's explicit yes, with its estimate.

**Tech Stack:**
- **The engines:**
  - `Azure.AI.Projects` 2.x: prompt agent versions and the project's Responses client
  - `Microsoft.Extensions.AI.OpenAI` (`AsIChatClient`)
  - `Anthropic.Foundry`, plus the Anthropic SDK's `IChatClient`
  - `Azure.Identity`
- **Traces:** `Azure.Monitor.OpenTelemetry.Exporter`
- **Infrastructure:** Terraform with azurerm 5.x and azapi 2.x, run in WSL
- **Evaluation and analysis:** a Python 3 `eval/` folder, using `azure-ai-projects` 2.x's Evals
  API and `azure-identity`

**Spec:** `docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md`. This plan covers
§4.1, §4.5, §4.7, §5.2–5.4, §6, §7 (the live run), §8, §9 item 3, §10 and §11.

## Global Constraints

**The setup:**
- Everything from plans 1 and 2 holds: SDK and package pinning, 0 warnings, xunit.v3 on
  Microsoft.Testing.Platform, the commit identity and trailer, and never pushing.
- **Region and models:** eastus2.
  - The agent runs on `gpt-5.6-luna` (GPT) and `claude-haiku-4-5` (Claude), both Global
    Standard.
  - The judge is the same `gpt-5.6-luna` deployment.
  - Every capacity is small (spec §6.1).
- **Keyless.**
  - No key exists anywhere: `local_auth_enabled = false` on the Foundry resource, local
    authentication disabled on Application Insights, and no key in any output, file, log or
    transcript.
  - People and CI sign in through Entra, with the Azure CLI locally and OIDC in CI.
  - The roles are referenced by GUID. Foundry User is `53ca6127-db72-4b80-b1b0-d745d6d5456d`.
- **Identifiers stay out of public files.**
  - Subscription IDs, tenant IDs, object IDs, real resource names and suffixes, endpoints and
    emails are never committed.
  - Real values live in git-ignored `terraform.tfvars`, in environment variables or in GitHub
    environment secrets.
  - Public files use placeholders.
- **Terraform runs only in WSL** (`wsl.exe -d Ubuntu --exec bash -c '…'`), with
  `TF_DATA_DIR=$HOME/tfdata/<stack>`. Tests use `-backend=false` and mock providers. **Never
  `plan` or `apply` against Azure in Tasks 1–7.**
- **Nothing hourly** (spec §6.3):
  - no standard agent setup, no hosted agents, no agent memory
  - no safety evaluators
  - no Data Zone, no PTU

**The pre-registered study:**
- **Success:** `Success = Check.Passed && GateViolations == 0 && !InfraError && Outcome == "completed"`
  (plan 2's ruling).
- **Infra errors:** they are dropped from the pairs. A run is repeated only if the runbook says
  so, and the repeat is logged.
- **The comparison:** C1 = GPT − Claude in task success, paired by scenario on each scenario's
  mean over 3 passes.
  - **A difference** is declared only when the mean is at most −0.10 or at least +0.10 **and** the
    95% bootstrap interval excludes zero.
  - **The bootstrap:** 10,000 resamples, seed 20261004, percentiles by nearest rank.
  - **Otherwise** the verdict is "inconclusive at n scenarios".
- **Neutrality:** both engines get identical instructions (`AgentInstructions.Text`), tool schemas
  and `AgentSettings`, and a test proves it.

## Review Focus

1. **Error classification.** A real SDK error must be classified correctly:
   - 401, 403, DNS and network faults, ≥ 500, and Azure.Identity failures are infra (dropped)
   - 429 is throttled
   - a content-filter 400 is `content_filtered`
   - every other 400 is `engine_error`, the model's own failure

   Test it per SDK, with recorded or fake HTTP responses, in Tasks 1–2.
2. **The prompt agent can't drift from the code.** The GPT agent version must hold exactly
   `AgentInstructions.Text` and the six tool schemas. If the agent's live definition differs from
   the code, the run refuses to start. Test in Task 2.
3. **A study run uses only the frozen scenarios.** A paid study run with an edited scenario,
   changed engine settings, tools or instructions is refused. The runner checks the freeze
   manifest's hashes before the first model call. Test in Task 3.
4. **No secret reaches a public file.** No key, identifier or endpoint may reach a committed file,
   a transcript or a log. Static checks run in CI, and transcripts are scanned. Tests in Tasks 4
   and 7.
5. **The live CI workflow only runs from `main`.** It must not run from a pull request or a fork,
   and must not run without the protected environment. Its federated credential is limited to that
   environment. Test the workflow's static shape in Task 7.

---

### Task 1: Binding hardening from plan 2

**Files:**
- **Modify:** `src/Workshop.Agent/{Runner/ScenarioRunner.cs, Tools/WorkshopTools.cs, Engines/ChatClientEngine.cs, Engines/ServiceFailure.cs}`
- **Create:** `src/Workshop.Agent/Engines/AgentSettings.cs`
- **Tests:** in `tests/Workshop.Agent.Tests`

**Interfaces:**
- **Produces `AgentSettings`:** a static class holding `MaxOutputTokens = 4096` and
  `Temperature = null` (the model's default, stated). It also has
  `Sha256 { get; }` over a canonical JSON of the settings.
- **What `ChatClientEngine` sets:** `ChatOptions.MaxOutputTokens = AgentSettings.MaxOutputTokens`
  for every engine.
- **Produces a new outcome:** `EngineOutcome.Truncated = "truncated"`, set when the final model
  call's `FinishReason` is `length`. It is not `completed`, so success is 0.

- [ ] **Step 1: Write the failing tests.**
  - **The backstop race (N1):**
    `Backstop_firing_as_the_engine_finishes_still_writes_a_transcript`. The race is forced with an
    injectable clock or hook.
  - **The cut-off press (N3):** `Approved_press_cut_off_by_the_limit_keeps_its_record_and_is_not_a_violation`.
    The tool record for an approved destructive press is written **before** the request is sent,
    then updated with its outcome.
  - **Truncation:** `Final_call_ending_length_is_truncated_not_completed`.
  - **Settings sent:** `Every_engine_request_carries_AgentSettings`.
- [ ] **Step 2: Run the tests.** Expected: they fail.
- [ ] **Step 3: Implement the hardening.**
  - Catch the `ObjectDisposedException` around the backstop's cancel.
  - Write the record before the press.
  - Add `AgentSettings` and the `truncated` outcome.
  - Put `AgentSettings.Sha256` in the transcript.
- [ ] **Step 4: Run the full suite.** Expected: it passes, with 0 warnings.
- [ ] **Step 5: Commit.** Message:
  `fix(agent): the binding hardening for real engines — settings, truncation, and two races`.

### Task 2: The two real engines

**Files:**
- **Create:**
  - `src/Workshop.Agent/Engines/Foundry/{FoundryOptions.cs, PromptAgentProvisioner.cs, AgentReferenceChatClient.cs, GptEngineFactory.cs}`
  - `src/Workshop.Agent/Engines/Claude/{ClaudeEngineFactory.cs}`
  - `src/Workshop.Agent/Engines/SdkErrors.cs`, which maps SDK exceptions to `ThrottledException`,
    a service failure, `content_filtered` or `engine_error`
- **Modify:**
  - `Program.cs`: `--engine gpt|claude` become real
  - `Engines/ServiceFailure.cs`: it now matches on the real types
- **Tests:**
  - `tests/Workshop.Agent.Tests/RealEngines/*`, using a fake `HttpMessageHandler` or pipeline
    transport that replays recorded JSON
  - `tests/Workshop.Agent.Tests/NeutralityTests.cs`

**Interfaces:**
- **Produces the settings:**
  `FoundryOptions(Uri ProjectEndpoint, Uri ResourceEndpoint, string GptDeployment, string ClaudeDeployment, string AgentName)`.
  They are read from the environment variables `FWA_PROJECT_ENDPOINT`, `FWA_RESOURCE_ENDPOINT`,
  `FWA_GPT_DEPLOYMENT`, `FWA_CLAUDE_DEPLOYMENT` and `FWA_AGENT_NAME`, or from command-line options.
  A missing one is a clear error.
- **Produces the provisioner:**
  `PromptAgentProvisioner.EnsureAsync(FoundryOptions, IReadOnlyList<AIFunction> tools, TokenCredential) -> AgentVersionRef(string Name, string Version, string InstructionsSha256, string ToolsSha256)`.
  - It creates a new agent version only when the latest version's instructions or tool schemas
    differ from the code's. Otherwise it reuses that version.
  - After creating or reusing a version, it reads it back. If the read-back doesn't match, it
    throws `AgentDriftException` (Review Focus 2).
- **Produces the GPT engine:**
  `GptEngineFactory.Create(FoundryOptions, EngineRun run, TokenCredential) -> IAgentEngine`. It
  builds a `ChatClientEngine` over the project's Responses client for the agent, as `AsIChatClient()`.
  - **A forwarding layer.** `AgentReferenceChatClient` sits directly under the function-invocation
    layer. It removes `Instructions` and `Tools` from the outgoing `ChatOptions`, because the
    agent version holds them. The function loop keeps seeing the tools.
  - **Its name:** `Name = "gpt"`, and `Model` is the deployment's model name.
  - **The transcript** gains `Deployment` and `AgentVersion`.
- **Produces the Claude engine:**
  `ClaudeEngineFactory.Create(FoundryOptions, EngineRun run, TokenCredential) -> IAgentEngine`. It
  builds a `ChatClientEngine` over the Anthropic SDK's `IChatClient` for
  `AnthropicFoundryClient`, which authenticates with an identity token for the scope
  `https://ai.azure.com/.default`.
  - **Its name:** `Name = "claude"`.
  - **Its instructions and tools** are sent per request, as plan 2's engine already does.
- **No SDK-level retries.** Both SDKs' own retries are turned off (`MaxRetries = 0`, or a no-retry
  pipeline policy), so `ThrottleRetryChatClient`'s 60-second budget is the only retry.
- **`SdkErrors` maps errors per SDK** (Review Focus 1):

  | What comes back | Outcome |
  |---|---|
  | 429, with `Retry-After` or `retry-after-ms` | `ThrottledException` |
  | 401, 403, ≥ 500, `HttpRequestException`, a timeout, or an Azure.Identity failure | a service failure, so infra |
  | 400 with an Azure `content_filter` error code | `content_filtered` |
  | 400 with an Anthropic `invalid_request_error` that names content policy | `content_filtered` |
  | any other 400 or 404 | `engine_error`, with the body's error code in `Error` |

  The plan-2 stand-in exception tests are replaced by tests using the real types.

**Neutrality** (spec §5, Review Focus 2). `NeutralityTests` is a test class with two tests:
- **`Both_engines_get_identical_instructions_tools_and_settings`:** it records each engine's
  outgoing first request through the fake transports. Claude's per-request payload and GPT's agent
  version definition must hold the same instruction text, the same tool names, descriptions and
  JSON schemas, and the same `max_output_tokens`.
- **`Agent_drift_is_refused`:** the read-back returns different instructions, and
  `AgentDriftException` follows.

**Verify first.** Before building on any of these, confirm the API and record what you confirmed
in the report:
- the exact names in `Azure.AI.Projects` 2.x: the agent administration client, the
  declarative/prompt agent definition, the function tool, and the Responses client for an agent
- whether `AsIChatClient()` over that Responses client accepts an agent reference, and whether
  the request may carry tools or instructions
- the Anthropic SDK's `IChatClient` adapter and `AnthropicFoundryClient`

If the agent-reference path can't carry client-side function calls through `IChatClient`, stop
and report NEEDS_CONTEXT, with the evidence. The fallback is a dedicated `IAgentEngine` over the
raw Responses loop, which reimplements plan 2's rules, and the controller decides on it.

- [ ] **Step 1: Verify the APIs.** Read the packages' docs and XML documentation, and use a
  scratch compile if needed. No network calls to Azure.
- [ ] **Step 2: Write the failing tests.** These cover the classification table per SDK, the
  provisioner's reuse, new-version and drift cases, the neutrality tests, the stripping in
  `AgentReferenceChatClient`, and the retries being off.
- [ ] **Step 3: Implement the engines.**
- [ ] **Step 4: Run the tests.** Expected: they pass, with 0 warnings. `--engine gpt` without the
  `FWA_*` variables fails with the missing name.
- [ ] **Step 5: Commit.** Message:
  `feat(agent): the GPT prompt-agent engine and the Claude engine, keyless and neutral`.

### Task 3: The freeze

**Files:**
- **Create:**
  - `src/Workshop.Agent/Scenarios/Freeze.cs`
  - `scenarios/README.md`, a short note on what the freeze covers
- **Modify:** `Program.cs`, `Runner/ScenarioRunner.cs`
- **Test:** `tests/Workshop.Agent.Tests/FreezeTests.cs`

**Interfaces:**
- **Produces the command:** `Workshop.Agent scenarios freeze <dir>`. It writes
  `<dir>/freeze.json` with these fields:
  - `frozenOn`
  - a SHA-256 for each scenario file
  - `instructionsSha256`
  - `settingsSha256`
  - `toolsSha256`, over the six tools' names, descriptions and schemas
  - `decisionRule`: the threshold 0.10, seed 20261004, 10,000 resamples and 3 passes
- **Produces the `--study` flag on `run`.** It requires `freeze.json`, and refuses with the first
  mismatching item if any of these differs from the freeze:
  - a scenario file
  - the instructions
  - the settings
  - the tools

  `--study` also forces `--passes 3`, and makes the output directory name carry the freeze's
  short hash.

- [ ] **Step 1: Write the failing tests:**
  - `Freeze_writes_every_hash`
  - `Study_refuses_an_edited_scenario`
  - `Study_refuses_changed_instructions_settings_or_tools`
  - `Study_runs_when_everything_matches`
  - `Non_study_runs_ignore_the_freeze`
- [ ] **Step 2: Run the tests.** Expected: they fail.
- [ ] **Step 3: Implement the freeze.** Do **not** create `scenarios/freeze.json` in this task.
  The owner's read-through comes first (runbook step 2).
- [ ] **Step 4: Run the tests.** Expected: they pass.
- [ ] **Step 5: Commit.** Message: `feat(agent): freeze the scenarios, and refuse a study run that drifted`.

### Task 4: The Terraform stacks

**Files:**
- **Create:**
  - `infra/bootstrap/{versions.tf, variables.tf, main.tf, outputs.tf, tests/bootstrap.tftest.hcl, README.md}`
  - `infra/foundry/{versions.tf, backend.tf, variables.tf, main.tf, outputs.tf, tests/foundry.tftest.hcl, README.md}`
  - `tests/infra/StaticInfraTests.cs` (xunit), or a small script run in CI
- **Modify:** `.github/workflows/ci.yml`, adding a `terraform` job on `ubuntu-latest` that runs
  `fmt -check`, `init -backend=false`, `validate` and `test` for both stacks

**What each stack creates** (spec §6.1):
- **`infra/bootstrap`,** applied by the owner, with local state at first:
  - **A resource group,** `rg-fwa-bootstrap`.
  - **A storage account for state,** with:
    - shared-key access off, and `default_to_oauth_authentication` on
    - blob versioning on
    - a container, `tfstate-foundry`
  - **The owner's role:** `Storage Blob Data Contributor` on that container.
  - **The CI identity:**
    - an Entra application, its service principal, and **one federated credential**
    - the credential's subject is `repo:<owner>/<repo>:environment:live-eval`, with audience
      `api://AzureADTokenExchange`
  - **Outputs:** `ci_client_id`, `tenant_id` and `subscription_id`, all marked sensitive, and
    `state_storage_account`.

  The README tells the owner to copy them into the GitHub environment `live-eval` as secrets,
  never into a file.
- **`infra/foundry`,** with azurerm state in the bootstrap's container:
  - **The resource group,** `rg-fwa-foundry`.
  - **A random suffix.**
  - **The Foundry resource** (`azurerm_cognitive_account`):
    - kind `AIServices` and SKU `S0`, in eastus2
    - `custom_subdomain_name = "fwa-<suffix>"`
    - `local_auth_enabled = false` and `project_management_enabled = true`
    - a system-assigned identity
  - **The project:** `azurerm_cognitive_account_project`.
  - **The GPT deployment:** `azurerm_cognitive_deployment` for `gpt-5.6-luna`, Global Standard,
    with a capacity variable defaulting to 50 (thousands of TPM). The model version is a pinned
    variable, and the upgrade option is NoAutoUpgrade.
  - **The Claude deployment:** `azapi_resource` of type
    `Microsoft.CognitiveServices/accounts/deployments@<pinned preview>`, with:
    - format `Anthropic` and model `claude-haiku-4-5`
    - Global Standard, with a capacity variable
    - `modelProviderData`, taken from variables (organisation, country code, industry)
    - schema validation off, which the starter kit requires

    It has a `depends_on` on the GPT deployment, because deployments must be serialised.
  - **Monitoring:**
    - Log Analytics
    - Application Insights, workspace-based, with `local_authentication_disabled = true`
    - the project's Application Insights connection, an `azapi_resource` of category `AppInsights`
      whose exact type and fields are to be verified
  - **The roles:**
    - Foundry User on the project for the owner and for the CI service principal
    - Foundry User on the resource for the project's managed identity
    - `Monitoring Metrics Publisher` on Application Insights for the owner and for CI, so the
      exporter can ingest with Entra
  - **Outputs:** the project endpoint, the resource endpoint, the deployment names and the
    Application Insights connection string. The connection string holds no key once local auth is
    off, but it is still marked sensitive.
  - **Tags:** `project = foundry-workshop-agent` on every resource.

**Tests and static checks** (Review Focus 4):
- **`terraform test` with mock providers, one assertion each for:**
  - local auth off on both resources
  - the subdomain set
  - project management on
  - both deployments' SKU and model
  - the role GUIDs and scopes
  - the federated subject
  - the tags
- **Static checks over `infra/`:**
  - no output of the form `primary_key`, `*_key`, `connection_string` or `instrumentation_key`
    is left unmarked as sensitive
  - no GUID or real-looking resource name is in a `.tf` default
  - `terraform.tfvars` is git-ignored

- [ ] **Step 1: Verify the resources.** Check the azurerm 5.x resource and argument names
  (`azurerm_cognitive_account_project`, `project_management_enabled`, and the Application
  Insights local-auth argument) and the azapi deployment and connection types against the
  provider docs, then record them in the report.
- [ ] **Step 2: Write the tests and the static checks.** Expected: they fail.
- [ ] **Step 3: Write the stacks and both READMEs.** Each README covers plan, apply, destroy, and
  purging the soft-deleted account, with placeholders only.
- [ ] **Step 4: Run the checks in WSL** with `TF_DATA_DIR`: `fmt -check`, `validate` and `test`
  for both stacks. Expected: they pass.
- [ ] **Step 5: Commit.** Message:
  `feat(infra): a bootstrap stack and a keyless Foundry stack, tested offline`.

### Task 5: Trace export to Application Insights

**Files:**
- **Create:** `src/Workshop.Agent/Telemetry/TraceExport.cs`
- **Modify:** `Program.cs`, adding `--trace`
- **Test:** `tests/Workshop.Agent.Tests/TraceExportTests.cs`

**Interfaces:**
- **Produces the exporter:**
  `TraceExport.Start(string connectionString, TokenCredential credential) -> IDisposable`. It
  starts a `TracerProvider` with these sources:
  - `Workshop.Agent`
  - Agent Framework's source, with its exact name verified
  - `Azure.AI.Projects.*`

  It sends them to `AddAzureMonitorTraceExporter`, with `Credential = credential` so ingestion
  uses Entra. It also sets the GenAI tracing switch, `Azure.Experimental.EnableGenAITracing`.
- **What `--trace` does:** it reads `FWA_APPINSIGHTS_CONNECTION_STRING`. Without that variable,
  `--trace` is refused with a clear message.
- **Content capture is off by default.** `--trace-content` turns it on, for development only.

- [ ] **Step 1: Write the failing tests:**
  - `Exporter_is_configured_with_the_credential_and_sources`, using an in-memory exporter
  - `Trace_refused_without_a_connection_string`
  - `Content_capture_off_by_default`
- [ ] **Step 2: Implement the exporter, run the tests, and commit.** Message:
  `feat(agent): export traces to Application Insights with Entra`.

### Task 6: The evaluation and the analysis

**Files:**
- **Create:**
  - `eval/requirements.txt`, pinning `azure-ai-projects` 2.x, `azure-identity` and `pytest`
  - `eval/fwa_eval/{dataset.py, run_eval.py, analysis.py, report.py, __main__.py}`
  - `eval/tests/*`
  - `eval/README.md`
- **Modify:** `.github/workflows/ci.yml`, adding a Python test step on `ubuntu-latest`

**Interfaces:**
- **Produces the dataset builder:** `dataset.build(transcripts_dir) -> list[dict]`. It writes one
  JSONL row per transcript, holding:
  - `query`: the task
  - `response`: the messages, as Foundry's agent evaluators expect them
  - `tool_definitions`: the six tools
  - `tool_calls`

  Rows marked `infraError` are left out.
- **Produces the evaluation run:**
  `run_eval.run(project_endpoint, judge_deployment, dataset_path, credential) -> dict`. It creates
  one cloud evaluation through the project's OpenAI client (`evals.create` and
  `evals.runs.create`).
  - **The evaluators:**
    - `builtin.tool_call_accuracy`
    - `builtin.task_adherence` (preview)
    - `builtin.intent_resolution` (preview)
  - **The judge:** `gpt-5.6-luna`.
  - **What it returns:** it polls until the run completes and returns the per-row scores.
  - **Keyless:** it uses `AzureCliCredential` locally and the OIDC credential in CI. No key is
    involved.
- **Produces the analysis:** `analysis.compare(transcripts_dir) -> dict`.
  - It takes each scenario's mean task success over its passes for each engine, and pairs the
    scenarios.
  - It computes C1 = GPT − Claude under the frozen rule (the 10,000-resample bootstrap, seed
    20261004 and nearest-rank percentiles, settled to 12 decimal places).
  - The verdict is `difference` or `inconclusive at n scenarios`, reporting n.
- **Produces the report:** `report.render(analysis, eval_scores, transcripts_dir) -> str`. It
  renders Markdown with:
  - the date, n and the freeze hash
  - the per-engine task success, tokens, cost and time per task
  - tool calls per task, gate violations and outcomes by kind
  - the evaluator scores, with preview ones labelled "(preview)"
  - the C1 verdict
  - the limits, as the spec's §10 and §12 allow

**Prices.** Costs come from the recorded tokens, at rates read on the day of the run. They are
fixed in `eval/fwa_eval/prices.py` with their source and date:
- `gpt-5.6-luna`: $0.20 / $1.20 per 1M tokens
- Claude Haiku 4.5: $1 / $5 per 1M tokens

- [ ] **Step 1: Write the failing pytest tests,** on recorded transcripts and a fake evals client:
  - the dataset shape, with infra rows left out
  - the bootstrap against a hand-computed small case
  - a mean of exactly ±0.10 counts as a difference
  - an interval touching zero is inconclusive
  - the report's sections and labels
  - a scan that the report contains no GUID, endpoint or key (Review Focus 4)
- [ ] **Step 2: Implement the package, run the tests, and commit.** Message:
  `feat(eval): Foundry evaluators over the transcripts, and the pre-registered comparison`.

### Task 7: The live CI workflow

**Files:**
- **Create:** `.github/workflows/live-eval.yml`
- **Test:** `tests/Workshop.Agent.Tests/WorkflowShapeTests.cs`, which parses the YAML

**The workflow** (spec §7, Review Focus 5):
- **When it runs:** `on: workflow_dispatch` only.
- **What it runs on:** a job with `if: github.ref == 'refs/heads/main'`, `environment: live-eval`
  and `runs-on: windows-latest`.
- **Its permissions:** `contents: read` and `id-token: write`.
- **Its steps:**
  1. Check out the code and set up .NET and Python.
  2. Sign in with `azure/login`, pinned by SHA, through OIDC with the environment's secrets.
  3. Run `Workshop.Agent run --engine gpt --study` over the frozen scenarios, with **1 pass**.
     This is the CI smoke version. The full study runs locally per the runbook.
  4. Run `python -m fwa_eval eval` and `report`.
  5. Post the report to the job summary.
  6. Scan the outputs for secrets before uploading them as an artifact.
- **Its limits:** a 30-minute timeout, and concurrency 1.

`WorkflowShapeTests` asserts the trigger, the `if`, the environment, the permissions, that every
action is pinned by SHA, and the timeout.

- [ ] **Step 1: Write the tests.** Expected: they fail.
- [ ] **Step 2: Write the workflow.** Run the tests. Expected: they pass.
- [ ] **Step 3: Commit.** Message:
  `feat(ci): a manual, main-only live evaluation on the GPT engine`.

### Task 8: The runbook (owner-gated, paid steps)

**Files:**
- **Create:** `docs/runbook-plan-3.md`, with every command, every expected output and every
  estimate. The runbook only reaches Azure when the owner says yes to a step.

**The steps.** Each one waits for the owner's yes.
1. **Merge Tasks 1–7** through a PR, once CI is green.
2. **The read-through, then the freeze.** The owner reads the 20 scenarios and
   `AgentInstructions.cs`. The items flagged for this read are s03's pattern and the two
   instruction sentences changed in plan 2's final review. After the owner says the scenarios
   are final, run `scenarios freeze`, then commit and merge `freeze.json`.
3. **Apply bootstrap.**
   - **The yes covers** the Entra application and its federated credential.
   - **First:** run `az account show`, which must be the personal account.
   - **The plan must show** only the resources in Task 4.
   - **Afterwards:** copy the outputs into the GitHub environment `live-eval` as secrets, and
     protect the environment to `main`.
4. **Apply foundry.**
   - **The yes covers accepting Anthropic's Marketplace terms.**
   - **The plan must show** only Task 4's resources.
   - **Afterwards:** confirm local auth is off on both resources, with `az … show`, without
     printing keys.
5. **Smoke test, about $0.10.**
   - One scenario on each engine, without `--study`.
   - Then check that the trace appears in Application Insights within about 5 minutes.
6. **Dry run, about $1.**
   - s05, s13 and s17 on both engines, with 1 pass each.
   - Measure the tokens per task, and replace §8's estimates in the runbook before the study.
   - If any engine ends `truncated`, or hits the tool budget on a correct path, stop and bring it
     to the owner. A change after the freeze means a new freeze, and is logged.
7. **The study.** It is 20 × 3 × 2 runs plus scoring, at roughly $3, with the exact figure
   from step 6.
   - It runs with `--study`, then the evaluation, then the report.
   - Infra-error runs are repeated once and logged. If they repeat, they are reported as dropped.
8. **Publish.**
   - Add a dated "Results" section to the README, worded as rendered.
   - Commit the report and the transcripts, after scanning them for secrets.
   - Merge it through a PR, then update the tracker.
9. **Leave it running or destroy it.** Nothing bills by the hour, so the stack may stay up. If the
   owner wants it gone, run destroy, purge, and confirm the resource group is gone.

- [ ] **Step 1: Write the runbook.** Use placeholders for every identifier. **Do not run any
  step of it in this task.**
- [ ] **Step 2: Commit.** Message: `docs: the plan-3 runbook, every step owner-gated`.
