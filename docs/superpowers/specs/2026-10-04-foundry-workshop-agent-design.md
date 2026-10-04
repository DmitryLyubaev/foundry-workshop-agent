# Foundry workshop agent — design

**Status: specification, awaiting the owner's approval (written 2026-10-04).** Nothing is built
or measured yet. Every figure below either comes from a named source on a stated date, or is
labelled as an estimate. Claims that could not be checked are marked *unverified*, and §11 lists
what to verify during implementation.

- **Where it sits.** This is project 3 of phase 1 of the owner's portfolio plan, in a new public
  repo, `foundry-workshop-agent`. Phase 1 covers Azure OpenAI and Microsoft Foundry.
- **What it reuses** from projects 1 and 2 in ReleaseLens: keyless Azure through the Azure CLI,
  Terraform run in WSL, pre-registered decision rules, and the owner's yes before anything that
  bills or publishes.
- **Where the facts come from.** All facts about Foundry were read on 2026-10-04 from
  learn.microsoft.com, NuGet, PyPI and the Azure Retail Prices API. The notes, with every source,
  are kept in the owner's private plan repo.

---

## 1. Purpose

Show, with measured and dated evidence, a real **Microsoft Foundry** agent (formerly Azure AI
Foundry) that drives a Windows desktop app **safely, through local function tools**, with the
Azure plumbing done the enterprise way:
- keyless identity
- Terraform
- quotas
- Foundry's own evaluators
- OpenTelemetry traces in Application Insights
- the same agent run on **GPT and on Claude**

**Success** means a reviewer can read the repo and see four things:
1. **The app becomes drivable without being rewritten.** A from-scratch WinForms app is made
   agent-drivable by a generic, self-describing HTTP endpoint.
2. **The agent acts only locally.** It acts only through that endpoint, on the owner's machine,
   with a human gate on destructive actions. Nothing in the cloud can reach the app.
3. **Measured results.** A fixed scenario set measures task success on both engines, with
   Foundry evaluator scores beside it, under a pre-registered rule, at a total cost of a few
   dollars.
4. **Keyless.** There is no key anywhere.

**Non-goals:**
- A remote MCP tool. The plan's read-only remote tool is project 5's `search_corpus`, which does
  not exist yet.
- Hosted agents, or Foundry's "standard" agent setup.
- Safety and red-team evaluators.
- Any claim beyond this app and these scenarios.

## 2. Decisions taken with the owner (2026-10-04)

| Topic | Decision |
|---|---|
| Demo idea | A self-describing desktop app, written from scratch for this repo. Every name and record in it is made up. |
| App domain | A small repair workshop: customers, devices, job cards and parts |
| UI | WinForms on .NET 10 |
| Agent client | C#; Foundry's evaluators run as a small Python step |
| Repo | `foundry-workshop-agent`, public, created only on the owner's yes |
| Approach | **A:** one agent definition on Microsoft Agent Framework, with two engines: GPT as a Foundry Agent Service prompt agent, and Claude deployed in Foundry and called through Agent Framework's Anthropic client |
| Models | Small against small: `gpt-5.6-luna` against Claude **Haiku 4.5**. The judge is `gpt-5.6-luna`. |
| Region | **eastus2.** Claude is not offered in any Asia-Pacific region. |

## 3. The workshop app

**3.1 Domain.** The app holds:
- customers
- devices (laptop, printer, phone and so on, each with its owner)
- job cards
- parts, with stock levels

A job card has:
- a device
- a fault description
- a status: `booked in`, `diagnosing`, `waiting on parts`, `in repair`, `ready`, `collected` or
  `cancelled`
- the parts used
- timestamped notes

The validation rules are part of the test surface. For example:
- a job cannot be `ready` with a part still on order
- `collected` needs `ready` first
- a used part must be in stock

**3.2 Storage.** The app keeps a local SQLite file, seeded from a committed script of made-up
records. Every run starts from a fresh copy, so runs are independent and repeatable.

**3.3 Screens.** About six:
- job card list, with filters
- job card detail and edit
- new job card
- customer list and detail
- device detail
- parts

Each control carries metadata: a stable ID, a label, its kind, and whether it is required or
destructive. It is set in one place per form.

**3.4 The describer.** One generic component walks the open form's control tree. It produces
the screen's description from the controls and their metadata:
- the fields, with their kind, value, options, and whether they are enabled and required
- the buttons, with a destructive flag
- the lists, with their rows

No screen is described by hand. A new screen needs its metadata and nothing else.

**3.5 The endpoint.** It uses `HttpListener` on `127.0.0.1` only.

| Route | Purpose |
|---|---|
| `GET /screens` | the screens that can be opened |
| `GET /screen` | the current screen's description |
| `POST /actions` | one action: `open` a screen, `set` a field, `select` a row or `press` a button |

- **Actions run through the real UI on the UI thread.** The agent can do only what a person
  could.
- **Each action returns:**
  - an outcome: `ok`, `validation_failed`, `not_found` or `disabled`
  - the app's own message
  - the new screen description

**3.6 Lock-down.**
- **Loopback only.** Binding to anything other than `127.0.0.1` is refused at start-up.
- **A token per launch.** Each launch writes a fresh random token to a file readable only by the
  current user, and every request must carry it in a header. That stops other users, and stops
  web pages in a browser: a page can send requests to localhost, but cannot read the token.
- **Narrow input.** There are no CORS headers, and no route evaluates or executes input. Unknown
  routes and actions are refused.
- **An audit log.** Every action is written to an append-only log: time, action, target, outcome.

## 4. The agent client

**4.1 One definition, two engines.** The agent is defined once: its instructions, six tools and
the approval rule. `--engine gpt|claude` selects how it runs.
- **gpt.** A Foundry Agent Service **prompt agent**, on the current Responses-based agents API.
  The service returns `function_call` items; the client executes them locally, and sends back a
  `function_call_output` with the same `call_id`. Built with Agent Framework's Foundry
  integration. The classic threads/runs agents API (`Azure.AI.Agents.Persistent`) is not used:
  it retires on 2027-03-31.
- **claude.** Claude Haiku 4.5 deployed in the same Foundry resource, called through Agent
  Framework's Anthropic integration and `Anthropic.Foundry`. Claude in Foundry speaks only the
  Anthropic Messages API, and a Foundry prompt agent cannot drive it. So **this engine's tool
  loop runs in the client, not in Agent Service**, and the write-up says so.

**4.2 The tools.** There are six, each a thin wrapper over the endpoint:
- `list_screens`
- `describe_screen`
- `open_screen`
- `set_field`
- `select_row`
- `press_button`

**4.3 The approval gate.**
- **Asking.** Pressing a button the app marks destructive needs a human approval through Agent
  Framework's approval support. Interactively, the owner is asked.
- **In the study,** each scenario scripts the answer: approve or deny.
- **A slip is reported.** A destructive press without approval is recorded as a gate violation,
  never hidden.

**4.4 Limits.**
- **Tool calls:** at most 25 per scenario.
- **Time:** at most 5 minutes per scenario, under the service's 10-minute expiry for a response.
- **Throttling (429):** waits for the time the service says, within a budget, as in project 1.
- **Content-filter blocks:** recorded as their own outcome.
- **When a limit is hit,** the scenario ends with that limit as its outcome.

**4.5 Keyless.**
- **Locally:** the client signs in as the owner, through the Azure CLI.
- **In CI:** it signs in as the CI identity, through OIDC.
- **The role** is **Foundry User**, formerly Azure AI User, at project scope.
- **No key exists:** key authentication is off on the resource. Agent Service and evaluations
  accept only Entra.

**4.6 Transcripts.** Each scenario run writes one JSON transcript, holding:
- the engine, the model and the deployment
- each turn's tool calls, with their arguments, outcomes and times
- tokens per turn
- the final reply
- the approval decisions
- the end-state result
- the outcome

Transcripts are inputs to the evaluators and to the write-up. They hold only made-up demo data.

**4.7 Traces.**
- **Spans.** OpenTelemetry spans cover the scenario, each model call and each local tool
  execution. Agent Framework emits most of them; the client adds an `ActivitySource` for the app
  actions.
- **Export.** `Azure.Monitor.OpenTelemetry.Exporter` sends them to the project's Application
  Insights, so one trace shows a whole scenario.

## 5. Scenarios and the measurement

**5.1 The scenario set.** About 20 scenarios, written before any paid run.
- **What they cover:**
  - look-ups (read-only)
  - creating and updating
  - multi-step tasks
  - recovering from a validation message
  - destructive actions behind the gate, approved or denied
  - impossible or ambiguous requests, where the correct result is to stop and say so
- **What each scenario holds:**
  - an ID and a category
  - the task text
  - the starting data, which is the seed, plus any scenario-specific setup
  - the **expected end state**, as SQL checks on the app's database
  - the gate script
  - any rules for that scenario, such as which screens are off-limits
- **Who writes them.** Claude writes them, and the owner reads them through. They are then
  frozen with a SHA-256, like project 2's question set. The writer belongs to one of the two
  vendors being compared. That possible bias is stated, not removed.

**5.2 Metrics.** Measured per scenario run, over **3 passes** per scenario per engine.
- **Task success, the primary measure:** 1 when every end-state check passes and no gate
  violation occurred, otherwise 0. It is decided by the database, not by a model.
- **Foundry evaluators, which are descriptive:**
  - tool-call accuracy
  - task adherence (preview)
  - intent resolution (preview), if it runs on stored transcripts

  The judge is `gpt-5.6-luna`. Preview evaluators are labelled as preview.
- **Also recorded:**
  - tool calls per task
  - tokens, cost and time per task
  - gate violations (expected 0)
  - outcomes by kind

**5.3 The pre-registered comparison.**
- **What is compared.** C1 = GPT − Claude in task success, paired by scenario, using each
  scenario's mean over its passes.
- **The rule.**
  - A difference is declared only when the mean paired difference is at most −0.10 or at least
    +0.10, **and** its 95% bootstrap interval excludes zero.
  - The bootstrap resamples scenarios, with 10,000 resamples and seed 20261004, taking
    percentiles by nearest rank.
  - Anything else is "inconclusive at n scenarios".
- **Its basis** is the study rule from project 1, applied unchanged.
- **The expected outcome.** At about 20 scenarios an inconclusive result is likely, and is
  reported as such.

**5.4 Runs.** Each scenario run:
1. starts the app on a fresh database copy
2. runs the agent
3. checks the end state
4. saves the transcript

A run that fails for an infrastructure reason (the app failing to start, or auth) is an error,
not a task failure. It is recorded, and dropped from the pairs.

## 6. Infrastructure

**6.1 Two Terraform stacks,** run in WSL as in ReleaseLens. Each has its own state.
- **`infra/bootstrap`, applied by the owner:**
  - a resource group
  - a storage account for Terraform state, with shared keys off and Entra data access only
  - the CI identity: an Entra app registration with a federated credential limited to `main` and
    a protected GitHub environment
  - role assignments

  **Owner's yes needed** to create the app registration and credential.
- **`infra/foundry`:**
  - **The Foundry resource,** `Microsoft.CognitiveServices/accounts`, kind `AIServices`, with
    `allowProjectManagement` on, in eastus2. Local authentication is disabled, and the name takes
    a random suffix.
  - **One project.**
  - **Deployments, all Global Standard with small capacities:**
    - `gpt-5.6-luna`, for both the agent and the judge
    - **Claude Haiku 4.5**, through azapi, because azurerm cannot pass the provider data (azurerm
      issue #31140, open on 2026-10-04). **Applying it accepts Anthropic's Marketplace terms**,
      so its first apply needs the owner's explicit yes.
  - **Log Analytics and Application Insights,** connected to the project.
  - **Roles:**
    - the owner gets Foundry User at project scope
    - the CI identity gets Foundry User at project scope
    - the project's managed identity gets what tracing needs

**6.2 It can stay up.** No resource in either stack bills by the hour:
- the Foundry resource, the project, prompt agents and idle deployments are free
- tokens bill per use
- Application Insights bills per GB ingested

Unlike project 2's search service, there is no teardown between sessions. The README documents
destroy, including the purge that soft-deleted Foundry accounts need.

**6.3 Excluded by design:**

| Item | Why |
|---|---|
| Foundry's "standard" agent setup | It brings its own AI Search, at about US$97 a month on Basic |
| Hosted agents | About US$0.10 per vCPU-hour |
| Agent memory | |
| Safety evaluators | US$20 and US$60 per 1M tokens |
| Data Zone pricing | 1.1× |
| Provisioned (PTU) deployments | |

**6.4 Guards on the bill:**
- the owner's existing A$10 budget alert covers the subscription
- every resource is tagged `project = foundry-workshop-agent`
- small deployment capacities bound the spend rate
- every paid run waits for the owner's yes, with its estimate
- the live CI workflow is manual and runs from `main` only

## 7. CI

- **On every pull request** (GitHub Actions, free, no Azure):
  - build with 0 warnings
  - the app's describer, endpoint and action tests
  - the client's loop against a scripted fake model: the cap, the gate, 429 and content-filter
    handling
  - every scenario's end-state checks, run against a scripted correct path and a scripted wrong
    path
  - `terraform validate` and `terraform test`, with no Azure calls
  - static checks: no key outputs, key authentication off, no secrets
- **Live evaluation,** run by hand from `main` only:
  - **Where:** a Windows runner, signed in through OIDC as the CI identity.
  - **What it does:** starts the app, runs the scenarios on the GPT engine, scores them with
    Foundry's evaluators from the project's evals API, and posts a summary.
  - **Cost:** about US$1 a run (estimate).
- **Why the evaluators score stored transcripts.** Microsoft's evaluation GitHub Action invokes
  the agent itself, and cannot execute the local tools. So the evaluators score the transcripts
  this repo produces.

## 8. Costs (estimates, to be replaced by a measured dry run)

**Prices** (read 2026-10-04):

| Model | Input, per 1M tokens | Output, per 1M tokens | Billed through |
|---|---:|---:|---|
| `gpt-5.6-luna`, Global, eastus2 | US$0.20 | US$1.20 | Azure |
| Claude Haiku 4.5 | US$1 | US$5 | Azure Marketplace, at Anthropic's list price |

**Per scenario,** assuming about 25k input tokens over about 6 turns (an estimate):
- GPT: about US$0.01
- Claude: about US$0.03

**For the project:**

| Item | Estimate |
|---|---:|
| Development smoke tests | US$2–4 |
| The study (20 × 3 × 2 engines, plus scoring) | about US$3 |
| Each live CI evaluation | about US$1 |
| Application Insights | within the monthly free allowance (*unverified* for this account) |
| **Total** | **about US$10–15** |

A dry run of 2–3 scenarios measures the real token counts before the study, as in project 1,
and the estimates above are replaced by it.

## 9. Build order

Three plans, each merged before the next starts:
1. **The app.** Domain, storage, screens, describer, endpoint and lock-down, with tests. No
   Azure, no cost.
2. **The client and scenarios.** The client on a fake model, the scenario format, the end-state
   checks and the CI on pull requests. No Azure, no cost.
3. **Azure and the study.**
   - bootstrap, then the Foundry stack (each apply with the owner's yes)
   - the smoke test and dry run
   - freezing the scenarios after the owner reads them
   - the study, the evaluators and the live CI workflow
   - the write-up

## 10. What the README may claim afterwards

**It may claim:**
- the comparison's verdict, worded as rendered, with the date and the number of scenarios
- the per-engine figures and evaluator scores, as descriptive, with preview evaluators labelled
- that it runs keyless, and that the app can be driven only locally, with a token

**It may not claim:**
- that either model is better without a declared difference
- that Claude ran *inside* Foundry Agent Service, since its tool loop is client-side
- anything about other apps, other tasks or production use

## 11. To verify during implementation

- **Agent Framework's packages.** Their exact names and versions: `Microsoft.Agents.AI` 1.23.0
  is stable; the Foundry and Anthropic integrations may be prerelease. Also, whether the Foundry
  integration builds a *prompt agent* with client-side functions, or an ephemeral agent. The
  study needs a real prompt agent version for the GPT engine.
- **Model availability.** Whether `gpt-5.6-luna` and Claude Haiku 4.5 are deployable in eastus2
  on this subscription, and their default quotas.
- **Evaluators on stored transcripts.** Whether the evaluators accept transcripts of the shape
  this repo produces, through the cloud evaluation API. If not, use the `azure-ai-evaluation`
  SDK locally with the same judge.
- **WinForms on a runner.** Whether a WinForms app runs reliably on a hosted Windows runner.
- **Free trace allowance.** Application Insights' free ingestion allowance for this account.
- **The Claude role.** Which role the Claude deployment needs for Entra calls: Foundry User, or
  Cognitive Services User.

## 12. Risks

| Risk | Mitigation |
|---|---|
| Agent Framework's Anthropic package is prerelease and breaks | The fallback calls Claude through `Anthropic.Foundry` directly, with the same tool schemas and loop. It is stated in the write-up. |
| WinForms is unreliable on a CI runner | Live runs stay on the owner's PC, and CI scores uploaded transcripts. The plan's CI goal is then met by the scoring step. |
| Preview evaluators change or disappear | They are descriptive only. Task success, judged by the database, is the measure. |
| Small quotas slow the study | Accepted, as the cost guard. Throttling is waited out with a budget. |
| The scenario writer's vendor bias | Stated. The primary measure is the database end state, not a judge. |
| Marketplace terms or eligibility block the Claude deployment | The GPT engine and everything else still stands. The Claude half is reported as blocked, with the reason. |
| Soft-deleted names clash on re-create | A random suffix, and a documented purge. |
