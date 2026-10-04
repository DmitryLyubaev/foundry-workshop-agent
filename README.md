# foundry-workshop-agent

A small, made-up repair-workshop app for Windows (WinForms on .NET 10), built from scratch so
that a local agent can drive it through a generic, self-describing and locked-down HTTP endpoint.
Every screen's controls carry metadata, one generic describer turns the open screen into JSON, and
the agent acts only through the real controls, as a person would. Later plans add an agent client
that runs the same agent on GPT and on Claude in Microsoft Foundry, and measure it on a fixed set
of scenarios. The design is in
[docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md](docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md).

**Status: plan 2 of 3.** The app, its storage and rules, the describer, the endpoint and its
lock-down (plan 1), and the agent client with its 20 scenarios, run on a scripted fake model
(plan 2), are built and tested. The Azure study (plan 3) is not built yet. Nothing here uses
Azure, and every record in the database is made up.

## Run the app

```
dotnet run --project src/Workshop.App
```

or, once built, `Workshop.App.exe [--db <path>] [--port <n>] [--session-dir <dir>]`:

| Option | Default | Meaning |
|---|---|---|
| `--db <path>` | a fresh database, `%LOCALAPPDATA%\FoundryWorkshopAgent\runs\<timestamp>\workshop.db` | A workshop database file that must already exist, such as a fresh copy a test runner made. |
| `--port <n>` | `47811` | The endpoint's port on `127.0.0.1`. |
| `--session-dir <dir>` | `%LOCALAPPDATA%\FoundryWorkshopAgent` | Where `session.json` is written. |

The audit log, `audit.jsonl`, is written beside the database, so a run without `--db` has a
directory of its own; a runner that passes `--db` should give each run's copy its own directory.

| Exit code | Meaning (the reason goes to standard error, as `Workshop.App: <reason>`) |
|---|---|
| `0` | The window was closed normally. |
| `1` | The app could not start: the `--db` file is missing or is not a workshop database, or another instance holds the port. |
| `2` | A bad command line. |

A second instance never touches the first one's session file. A person moves between the screens
with the menu; the agent opens them by ID.

## The endpoint

Every request carries the launch's token in the header `X-Surface-Token`. The port and the token
are in `session.json`:

```powershell
$s = Get-Content "$env:LOCALAPPDATA\FoundryWorkshopAgent\session.json" -Raw | ConvertFrom-Json
Invoke-RestMethod "http://127.0.0.1:$($s.port)/screen" -Headers @{ 'X-Surface-Token' = $s.token }
```

| Route | Answers |
|---|---|
| `GET /screens` | the screens that can be opened: `[{"id","title"}]` |
| `GET /screen` | the current screen (shape below) |
| `POST /actions` | one action, as JSON (shapes below); answers its outcome, the app's message, and the screen after it |

## Contract for clients

This is what plan 2's client codes against. Plan 2 can reference `Workshop.Surface`'s records
(`ScreenDescription`, `ActionResult`, `SessionInfo`) and `SurfaceJson.Options` directly.

### Session and process

- **`session.json`** is `{"port":47811,"token":"…","pid":1234,"startedAt":"2026-10-04T09:00:00+00:00"}`.
  It is written only after the port is bound, readable only by the current Windows user, and
  deleted on a normal close. A file whose `pid` is not running is stale: its token is dead.
- **Stop the app with `CloseMainWindow`**, so the session file is removed. `Kill` leaves a stale file.

### JSON shapes

Names are camelCase. **An absent property means null:** `value`, `options`, `maxLength` and
`message` are left out when they have none.

| What | Shape |
|---|---|
| Screen | `{"id","title","fields":[…],"buttons":[…],"lists":[…]}`, each in tab order |
| Field | `{"id","label","kind","value"?,"options"?,"enabled","required","maxLength"?}`; `kind` is `text`, `number`, `choice`, `checkbox` or `date` |
| Button | `{"id","label","enabled","destructive"}` |
| List | `{"id","label","columns":[…],"rows":[{"key","cells":[…],"selected"}]}` |
| Action | `{"type":"open","screen"}`, `{"type":"set","field","value"}`, `{"type":"select","list","row"}` or `{"type":"press","button"}`; other properties are ignored, a repeated one is refused |
| Result | `{"outcome","message"?,"screen"}`, where `screen` is the current screen after the action |
| Error | `{"error":"<code>"}` |

**Values.** A `set` value is a string; a JSON number or boolean is also accepted, as its JSON text.

| Kind | Accepts |
|---|---|
| `text` | Any text up to `maxLength`, on one line. |
| `number` | A whole number within the control's range. |
| `choice` | Exactly one option's text, case-sensitive. |
| `checkbox` | `true` or `false`. |
| `date` | `yyyy-MM-dd`. |

### Outcomes

| Outcome | Meaning | Messages |
|---|---|---|
| `ok` | The action ran. A press may carry the app's news. | e.g. `Added on order: not enough in stock.` |
| `validation_failed` | A value or a rule was refused; nothing changed. | the setter's or the app's own message (below) |
| `not_found` | No such screen, or no such field, list, row or button on the current screen. | The six below, with the name or key the client sent in `<…>`. |
| `disabled` | The control is disabled. | `<Label> is disabled.` |

The six `not_found` messages are: `There is no screen '<id>'.`, `The screen '<id>' could not be
opened.`, `There is no field '<id>' on this screen.`, `There is no list '<id>' on this screen.`,
`<List label> has no row '<key>'.` (for example `Jobs has no row 'J-9'.`) and `There is no button
'<id>' on this screen.`

A refused `set` answers one of: `<Label> needs a whole number.`, `<Label> must be between <min>
and <max>.`, `<Label> must be one of: <options>.`, `<Label> needs true or false.`, `<Label> needs a
date as yyyy-MM-dd.`, `<Label> can be at most <n> characters.`, `<Label> must be on one line.`

### HTTP errors

| Status | `error` |
|---|---|
| 403 | `not_loopback`, `bad_host` |
| 401 | `unauthorized` |
| 404 | `not_found` |
| 405 | `method_not_allowed`, with an `Allow` header |
| 415 | `unsupported_media_type` (the body must be `application/json`) |
| 413 | `payload_too_large` (over 64 KB) |
| 400 | `bad_request` (not one valid action) |
| 503 | `ui_timeout` (the UI thread was busy for more than 10 seconds) |
| 500 | `internal_error` |

**A 503 or a 500 on `POST /actions` does not mean "not done".** A 503 can arrive after the action
started, and it still completes; a 500 can follow an action that ran. Read `GET /screen` before
retrying, or the action may happen twice.

### Screens and controls

| Screen | Fields | Buttons | Lists |
|---|---|---|---|
| `job-list` (Jobs) | `search` (text), `status-filter` (choice) | `open-job`, `new-job` | `jobs` |
| `job-detail` (Job) | `job` (text, disabled), `status` (choice), `fault` (text, disabled), `part` (choice), `quantity` (number, 1–20), `note` (text, up to 1,000) | `save-status`, `add-part`, `fit-ordered-parts`, `add-note`, `cancel-job` (**destructive**) | `parts`, `notes` |
| `new-job` (New job) | `customer` (choice), `device` (choice: the chosen customer's devices), `fault` (text, 3 to 500) | `book-in` | |
| `customer-list` (Customers) | `search` (text) | `open-customer` | `customers` |
| `customer-detail` (Customer) | `name`, `phone` (text, disabled), `kind` (choice), `model`, `serial` (text, up to 100) | `add-device` | `devices` |
| `parts` (Parts) | `quantity` (number, 1–100) | `receive-stock` | `parts` |

- **A detail screen shows the current record:** the job or customer selected or opened most
  recently. Booking a job in opens the new job, which makes it the current job. With no current
  record, `open` on a detail screen answers `ok` and shows the list: check `screen.id`.
- **A list opens with the current record selected**, so selecting another row always changes it.
  A search or filter that hides the current row leaves no row selected, and the record stays
  current: clearing the search brings its row back unselected, `open` on the detail screen still
  shows it, and `open-job` or `open-customer` answers `Select a job first.` or `Select a customer
  first.` until a row is selected again.
- `open-job` and `open-customer` open the selected row; `book-in` opens the new job.

### Option text and row keys

| Where | Format | Example |
|---|---|---|
| `status` | a status name: `booked in`, `diagnosing`, `waiting on parts`, `in repair`, `ready`, `collected`, `cancelled` | `in repair` |
| `status-filter` | `all`, then the status names | `ready` |
| `customer` (new job) | `<ID> <name>` | `C-001 Sam Rivera` |
| `device` (new job) | `<ID> <model> (<kind>)` | `D-001 Aster Book 14 (laptop)` |
| `part` (job) | `<ID> <name>` | `P-02 Phone screen assembly` |
| `kind` | `laptop`, `desktop`, `phone`, `tablet`, `printer`, `other` | `phone` |
| `job` field | `<job ID>: <model> (<kind>) for <customer>` | `J-1008: Inkwell 300 (printer) for Henderson Family` |

| List | Row key | Columns |
|---|---|---|
| `jobs` | the job ID, `J-1008` | Job, Customer, Device, Status |
| `customers` | the customer ID, `C-008` | Customer, Name, Phone |
| `devices` | the device ID, `D-011` | Device, Kind, Model, Serial |
| `parts` (parts screen) | the part ID, `P-04` | Part, Name, Stock |
| `parts` (job) | `<part ID>#<n>`, the part's nth line on the job, `P-04#1` | Part, Name, Quantity, State (`fitted` or `on order`) |
| `notes` | `note-<n>`, oldest first from 1 | At (UTC), Note; the `At (UTC)` cell is formatted `yyyy-MM-dd HH:mm` |

### Status changes, and the one destructive action

| From | May become |
|---|---|
| booked in | diagnosing, cancelled |
| diagnosing | waiting on parts, in repair, cancelled |
| waiting on parts | in repair, cancelled |
| in repair | waiting on parts, ready, cancelled |
| ready | collected, in repair |
| collected, cancelled | nothing |

**The only path to `cancelled` is the destructive `cancel-job` button.** `save-status` with
`cancelled` is refused, `validation_failed` with `Use Cancel job to cancel a job.`, and the job is
unchanged; the option stays in the list so a cancelled job shows its status. `collected` is not
destructive: it is the normal end of a job, reached only from `ready`.

### The app's messages

| Press | Outcome | Message |
|---|---|---|
| a button that needs a selection | `validation_failed` | `Select a job first.`, `Select a customer first.`, `Select a device first.`, `Select a part first.`, `Select a status first.` |
| `save-status` | `validation_failed` | `Use Cancel job to cancel a job.`, `A job that is <from> cannot become <to>.`, `Job <ID> still has parts on order.` |
| `cancel-job` | `validation_failed` | `A job that is <ready, collected or cancelled> cannot become cancelled.` |
| `add-part` | `ok` | `Added on order: not enough in stock.` when the stock is short; none when fitted |
| `add-part` | `validation_failed` | `The parts of a job that is ready cannot change. Set the status to in repair first.`, `The parts of a job that is <collected or cancelled> cannot change.` |
| `fit-ordered-parts` | `ok` | `Fitted <n> of <m> parts on order.`, `Job <ID> has no parts on order.` |
| `fit-ordered-parts` | `validation_failed` | `The parts of a job that is ready cannot change. Set the status to in repair first.`, `The parts of a job that is <collected or cancelled> cannot change.` |
| `add-note` | `validation_failed` | `A note cannot be empty.` |
| `book-in` | `validation_failed` | `The fault must be at least 3 characters.` |
| `add-device` | `validation_failed` | `The kind must be one of: laptop, desktop, phone, tablet, printer, other.`, `The model cannot be empty.`, `The serial cannot be empty.` |

## Agent client

`src/Workshop.Agent` drives the app through six tools over the endpoint (`list_screens`,
`describe_screen`, `open_screen`, `set_field`, `select_row`, `press_button`). A press of a button
the current screen flags destructive needs an approval first; a denied press never reaches the app.
Plan 2's only engine is `fake`: a scripted model, offline and free.

```
dotnet run --project src/Workshop.Agent -- run --engine fake --scenarios scenarios --script-dir tests/Workshop.Agent.Tests/Scripts [--only s05] [--passes 3] [--out <dir>]
dotnet run --project src/Workshop.Agent -- scenarios check scenarios
```

Each run starts the app on a fresh seeded database in its own temporary directory, with the
scenario's setup applied, on a fresh port; runs the engine, within 25 tool calls and 5 minutes (an
engine still running 30 s past the limit is ended by the runner, as `time_limit`); always closes the
app (it also runs in a kill-on-close job, so it ends with the runner); then counts gate violations
from the app's audit log and checks the end state on the database. It writes
`<out>/<scenario>.<engine>.p<pass>.json`, a transcript with the task, the SHA-256 of the agent's
instructions and of its model settings (`AgentSettings`: at most 4,096 output tokens a call, and
each model's own default temperature, the same for every engine), the model calls and their tokens,
every tool call with its outcome, its approval, the model call that asked for it and the result the
model was given, the final reply, the checks, and `success`: the run completed (outcome
`completed`), every check passed and no destructive press skipped the gate. A run that hit a limit,
was filtered, ended on an answer cut off at the output-token limit (outcome `truncated`), was
throttled or failed is not a success, even when the database happens to be right. An app that cannot
start, an endpoint that fails, or the model's service failing (network, credentials, or a 401, 403
or 5xx answer: outcome `service_error`, with the model calls kept) is an infrastructure error
(`infraError`), never a task failure. `--engine gpt` and `--engine claude` arrive in plan 3.

## Security model

- **Loopback only.** The endpoint is constructed for `127.0.0.1` only and refuses any other
  address. It holds eight URL prefixes, the root and every route's sub-path (`/`, `/screens/`,
  `/screen/`, `/actions/`) under both `127.0.0.1:<port>` and `localhost:<port>`, so no other
  process can register a route and capture a token. HTTP.sys listens on every local address for
  the port whatever the prefixes say, so every request from a non-loopback address is answered
  `403` (`not_loopback`) before any other check. Windows Firewall blocks unsolicited inbound
  connections by default; the app adds no firewall rule and needs no URL ACL or administrator rights.
- **A token per launch.** Each start writes a fresh random token to `session.json`, a file only
  the current Windows user can read. A web page can send requests to localhost, but cannot read
  the token. The file is deleted on a normal close, and a stale one from a crash is replaced.
- **`Host` check.** A request whose `Host` is not `127.0.0.1:<port>` or `localhost:<port>` is
  refused `403`, which stops DNS-rebinding pages.
- **No CORS, narrow input.** No CORS headers are sent, no route evaluates or executes input,
  bodies are size-limited JSON, and unknown routes, methods and actions are refused.
- **Serialised UI actions.** Actions run one at a time on the UI thread, through the real
  controls, so the agent can do only what a person could. The app never opens a modal dialog.
- **An audit log.** Every action is appended to `audit.jsonl`: time, type, target, value and
  outcome. If the log cannot be written, the failure is traced and the action's result is still
  answered, because the action has already happened.

**Accepted residual risks.** The design names HttpListener (spec §3.5), and both risks below are
contained by default settings:

1. **HTTP.sys parses requests from the LAN.** Its request parsing runs before the `not_loopback`
   check, with only Windows Firewall in front of it. A socket bound to `127.0.0.1` would not
   accept such connections at all.
2. **An administrator-made wildcard could capture tokens.** HTTP.sys routes a strong wildcard
   (`http://+:<port>/`) before explicit host names, and such a registration does not conflict with
   the endpoint's. If an administrator has created a URL ACL for one that another user can use,
   that user could receive requests, and their tokens. None exists by default
   (`netsh http show urlacl` lists them), and creating one needs administrator rights.

## Build and test

```
dotnet build -c Release
dotnet test -c Release
```

The tests include end-to-end runs of the app driven through its endpoint, runs of the exe
itself for its exit codes, and each of the 20 scenarios' correct and wrong scripted runs through
the real engine and app.

## Licence

Apache-2.0. See [LICENSE](LICENSE).
