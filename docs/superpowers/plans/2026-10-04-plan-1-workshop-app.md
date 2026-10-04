# Plan 1: The workshop app — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A from-scratch WinForms repair-workshop app that any local client can drive, through a
generic, self-describing and locked-down HTTP endpoint, with no Azure involved.

**Architecture:** Three projects.
- `Workshop.Core` holds the domain, SQLite storage and business rules, with no UI.
- `Workshop.Surface` is a generic agent surface for any WinForms app: control metadata, the screen
  describer, the action executor and the HTTP endpoint. It knows nothing about workshops.
- `Workshop.App` is the WinForms app. Its screens carry Surface metadata and call Core.

**Tech Stack:**
- .NET 10: WinForms, `net10.0-windows`
- `Microsoft.Data.Sqlite` and Dapper, for storage
- `HttpListener` and System.Text.Json, for the endpoint
- xunit.v3, for tests
- GitHub Actions on `windows-latest`, for CI

**Spec:** `docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md`: §3 in full, §7
(the pull-request CI), and §9 item 1.

## Global Constraints

**Build and code:**
- `global.json` pins SDK `10.0.201` with `rollForward: latestFeature`. `Directory.Build.props`
  sets `Nullable` enable, `TreatWarningsAsErrors` true, `EnforceCodeStyleInBuild` true and
  `ImplicitUsings` enable.
- Package versions are managed centrally in `Directory.Packages.props`, with transitive pinning on.
- Use the latest stable version of each package, and record it in that file.
- Tests use xunit.v3.
- `.gitattributes`: `* text=auto eol=lf`, `*.sln text eol=crlf`, `*.ps1 text eol=crlf`.

**What the app may contain:**
- Nothing taken from any other codebase or dataset: no code, names, schemas or data. Every record is made up.
- No Azure, no network calls except the endpoint's own loopback listener, and no secrets.

**The endpoint** (spec §3.5–3.6):
- It binds `127.0.0.1` only, and refuses any other address at start-up.
- Every request must carry the per-launch token in the header `X-Surface-Token`.
- Requests whose `Host` is not `127.0.0.1:<port>` or `localhost:<port>` are refused.
- It sends no CORS headers, and no route evaluates or executes input.
- Action outcomes are exactly `ok`, `validation_failed`, `not_found` and `disabled`. Every action
  is appended to the audit log.
- Actions run on the UI thread, through the real controls (`PerformClick`, setting the
  control's value). The app never opens a modal dialog during an action.

**Commits:**
- The repo-local identity is `Dmitry Lyubaev <Dmitry.Lyubaev@gmail.com>`. Never use `--author`
  or `git config --global`.
- Every message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Never push.

**Licence:** Apache-2.0, as ReleaseLens uses.

## Review Focus

1. **A blocked UI thread.** If something blocks the UI thread during an action, a request must not
   hang forever. After 10 seconds the endpoint answers `503`, with `{"error":"ui_timeout"}`, and
   keeps serving. Test in Task 4.
2. **A second instance, or a stale session file.**
   - A second app instance on the same port must fail at start-up with a clear message, and must
     **not** overwrite the first instance's session file.
   - A stale file from a crashed run is replaced.
   - Every launch gets a new token.

   Test in Task 4.
3. **Bad values are refused, not thrown.** These return `validation_failed` with a readable
   message, never a 500 or an exception:
   - text in a number field
   - a choice that isn't among the options
   - a date that won't parse
   - text over the field's maximum length

   Test in Task 3.
4. **Concurrent requests are served one at a time.** Two parallel `POST /actions` both complete,
   and the end state equals running them in sequence. Test in Task 4.
5. **Every interactive control is described.** Every control a person can use on any screen has
   metadata, so nothing usable is invisible to the agent, and nothing without metadata is
   described. Test in Task 5.

---

## File structure

```
foundry-workshop-agent/
  global.json, Directory.Build.props, Directory.Packages.props, .gitignore, .gitattributes,
  LICENSE, README.md, FoundryWorkshopAgent.slnx
  .github/workflows/ci.yml
  src/Workshop.Core/            domain records, WorkshopDb (schema, seed, fresh copy), JobService (rules)
    Sql/schema.sql, Sql/seed.sql   embedded resources
  src/Workshop.Surface/         ControlMeta + Surface (metadata API), ScreenDescriber, descriptions,
                                ActionExecutor, SurfaceFeedback, IScreenNavigator, SurfaceEndpoint,
                                SessionFile, AuditLog
  src/Workshop.App/             Program, MainForm (shell), Screens/*.cs (UserControls), WorkshopNavigator
  tests/Workshop.Core.Tests/
  tests/Workshop.Surface.Tests/ test forms built in code, run on a dedicated STA thread
  tests/Workshop.App.Tests/     metadata coverage, end-to-end through HTTP
```

`Workshop.Surface` and `Workshop.App` target `net10.0-windows` with `UseWindowsForms` on.
`Workshop.Core` targets `net10.0`.

---

### Task 1: Repo skeleton, domain, storage and rules

**Files:**
- **Create:**
  - the root files listed above, `src/Workshop.Core/**` and `tests/Workshop.Core.Tests/**`
  - `.github/workflows/ci.yml`
  - `README.md`: a stub, completed in Task 5
  - `LICENSE`: Apache-2.0, copied from the standard text
- **Test:** `tests/Workshop.Core.Tests/JobServiceTests.cs`, `WorkshopDbTests.cs`

**Interfaces:**
- **Produces the domain records**, all in `Workshop.Core`:
  - `Customer(string Id, string Name, string Phone)`
  - `Device(string Id, string CustomerId, string Kind, string Model, string Serial)`
  - `JobCard(string Id, string DeviceId, string Fault, JobStatus Status, DateTimeOffset BookedAt)`
  - `JobPart(string JobId, string PartId, int Quantity, PartState State)`
  - `Note(string JobId, DateTimeOffset At, string Text)`
  - `Part(string Id, string Name, int Stock)`
- **Produces the enums:**
  - `JobStatus { BookedIn, Diagnosing, WaitingOnParts, InRepair, Ready, Collected, Cancelled }`
  - `PartState { Fitted, OnOrder }`
- **Produces the display names** in `JobStatusNames`: `"booked in"`, `"diagnosing"`,
  `"waiting on parts"`, `"in repair"`, `"ready"`, `"collected"` and `"cancelled"`, with
  `Parse(string) -> JobStatus?`.
- **Produces `WorkshopDb`:**
  - `static WorkshopDb CreateFresh(string path)` creates a new file from `schema.sql` and
    `seed.sql`. If the file already exists, it throws.
  - `SqliteConnection Open()`
- **Produces the result type:**
  `RuleResult { bool Ok; string? Message }`, with `RuleResult.Success` and
  `RuleResult.Fail(string)`.
- **Produces `JobService(WorkshopDb db)`:**
  - `RuleResult SetStatus(string jobId, JobStatus to)`
  - `RuleResult AddPart(string jobId, string partId, int quantity)`
  - `RuleResult FitOrderedParts(string jobId)`
  - `RuleResult AddNote(string jobId, string text)`
  - `RuleResult BookIn(string deviceId, string fault, out string? jobId)`
  - `RuleResult Cancel(string jobId)`
  - `RuleResult ReceiveStock(string partId, int quantity)`
  - `RuleResult AddDevice(string customerId, string kind, string model, string serial, out string? deviceId)`
  - read methods, for the screens: `Jobs(JobStatus? status, string? search)`, `Job(id)`,
    `JobParts(id)`, `Notes(id)`, `Customers(search)`, `Customer(id)`, `DevicesOf(customerId)`,
    `Device(id)` and `Parts()`
- **IDs:** `J-1001…`, `C-001…`, `D-001…` and `P-01…`. New IDs continue each sequence.

**Rules** (spec §3.1). The allowed transitions:

| From | To |
|---|---|
| booked in | diagnosing, cancelled |
| diagnosing | waiting on parts, in repair, cancelled |
| waiting on parts | in repair, cancelled |
| in repair | waiting on parts, ready, cancelled |
| ready | collected, in repair |
| collected, cancelled | nothing |

- **Status changes:**
  - **Readiness.** `Ready` is refused while any part on the job is `OnOrder`, with the message
    `"Job J-… still has parts on order."`
  - **Other refused transitions.** Any other transition not in the table is refused, with
    `"A job that is <from> cannot become <to>."`
- **`AddPart`:**
  - when `quantity <= stock`, it is `Fitted` and the stock goes down
  - otherwise it is `OnOrder`, with no stock change, and still returns success with the message
    `"Added on order: not enough in stock."`
  - a quantity of less than 1 is refused
- **`FitOrderedParts`** fits each on-order part whose stock now covers it.
- **Text limits:**
  - `AddNote` refuses empty or whitespace text, and text over 1,000 characters
  - `BookIn` refuses a fault shorter than 3 or longer than 500 characters

- [ ] **Step 1: Write the root files and the solution.** Pin the actions to the same SHAs
  ReleaseLens uses: `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1` and
  `actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68 # v6.0.0`.
  - **`ci.yml`** runs on `pull_request` and `push` to `main`, on `windows-latest`, with
    `permissions: contents: read`. It runs `dotnet build -c Release` and then
    `dotnet test -c Release --no-build`.
  - **`.gitignore`** covers .NET output, `*.db` and `TestResults/`.
- [ ] **Step 2: Write the failing tests.**
  - `WorkshopDbTests`:
    - `CreateFresh_seeds_known_counts`: exactly the seed's counts of customers, devices, jobs and
      parts. The seed has 8 customers, 12 devices, 15 jobs and 10 parts, with at least one job in
      every status and two parts with stock 0.
    - The seed includes a customer `Sam Rivera` with a laptop, and a customer `Henderson Family`
      with a printer whose job is `diagnosing`. These are the spec's examples.
    - `CreateFresh_refuses_an_existing_file`
    - `Two_fresh_copies_are_independent`
  - `JobServiceTests`:
    - one test per row of the transition table, for both allowed and refused
    - `Ready_refused_with_a_part_on_order`, asserting the exact message
    - `AddPart_fits_and_decrements_when_in_stock`
    - `AddPart_goes_on_order_when_short`
    - `AddPart_refuses_quantity_below_one`
    - `FitOrderedParts_fits_after_ReceiveStock`
    - `AddNote_refuses_blank_and_over_1000`
    - `BookIn_assigns_the_next_job_id`
    - `Cancel_refused_for_collected`
- [ ] **Step 3: Run `dotnet test`.** Expected: the tests fail to compile, or fail.
- [ ] **Step 4: Implement the Core.** Implement the records, `schema.sql`, `seed.sql`,
  `WorkshopDb` and `JobService`. All writes go through `JobService`, in one transaction per call.
  Use Dapper.
- [ ] **Step 5: Run `dotnet build -c Release`, then `dotnet test`.** Expected: 0 warnings, and
  every test passes.
- [ ] **Step 6: Commit.** Message:
  `feat(core): repo skeleton, the workshop domain, SQLite storage and the job rules`.

### Task 2: Surface metadata and the screen describer

**Files:**
- **Create:**
  - `src/Workshop.Surface/{ControlMeta.cs, Surface.cs, Descriptions.cs, ScreenDescriber.cs}`
  - `tests/Workshop.Surface.Tests/{StaRunner.cs, TestForms.cs, ScreenDescriberTests.cs}`

**Interfaces:**
- **Produces the metadata API:**
  - `ControlMeta(string Id, string Label, bool Required = false, bool Destructive = false, int? MaxLength = null)`
  - the static class `Surface`:
    - `T Meta<T>(this T control, string id, string label, bool required = false, bool destructive = false, int? maxLength = null) where T : Control`.
      It is a fluent setter, stored in a `ConditionalWeakTable`. `Tag` is left untouched.
    - `void Screen(Control root, string id, string title)`
    - `ControlMeta? MetaOf(Control)`
    - `(string Id, string Title)? ScreenOf(Control)`
- **Produces the descriptions** (`Descriptions.cs`), which plan 2 consumes as JSON:
  - `ScreenDescription(string Id, string Title, IReadOnlyList<FieldDescription> Fields, IReadOnlyList<ButtonDescription> Buttons, IReadOnlyList<ListDescription> Lists)`
  - `FieldDescription(string Id, string Label, string Kind, string? Value, IReadOnlyList<string>? Options, bool Enabled, bool Required, int? MaxLength)`
  - `ButtonDescription(string Id, string Label, bool Enabled, bool Destructive)`
  - `ListDescription(string Id, string Label, IReadOnlyList<string> Columns, IReadOnlyList<RowDescription> Rows)`
  - `RowDescription(string Key, IReadOnlyList<string> Cells, bool Selected)`
- **Produces the JSON options:** `SurfaceJson.Options` has camelCase names and omits null
  values.
- **Produces the describer:** `ScreenDescriber.Describe(Control root) -> ScreenDescription`
- **Field kinds, inferred from the control type.** Only controls with metadata are described.

| Control | Kind | Value |
|---|---|---|
| `TextBox` | `text` | |
| `NumericUpDown` | `number` | the integer as a string |
| `ComboBox` with `DropDownList` style | `choice` | Options are the items' display text |
| `CheckBox` | `checkbox` | `"true"` or `"false"` |
| `DateTimePicker` | `date` | `yyyy-MM-dd` |
| `Button` | button | |
| `ListView` in details view | list | Each row's key is `ListViewItem.Name`. Columns are the headers. `Selected` reflects the selection. |

- **Exclusions.**
  - A control that isn't `Visible` is left out, whether it is hidden itself or sits inside a
    hidden container.
  - On a `TabControl`, only the selected tab's controls count.
- **Order.** Output follows the visual tab order.
- **Uniqueness.** Two controls with the same `Id` on one screen are a programming error.
  `Describe` throws `InvalidOperationException` naming the ID.

- [ ] **Step 1: Write `StaRunner`.** It runs an action on a dedicated STA thread with a
  WinForms message loop, so tests can build forms and handles.
- [ ] **Step 2: Write `ScreenDescriberTests`.** The test form includes one control of each kind,
  plus one control with no metadata, one hidden control and a two-tab `TabControl`. The tests:
  - `Describes_each_kind_with_value_options_and_flags`, with exact `FieldDescription` records
  - `Leaves_out_controls_without_metadata`
  - `Leaves_out_hidden_and_unselected_tab_controls`
  - `Disabled_controls_are_described_as_disabled`
  - `List_rows_carry_key_cells_and_selection`
  - `Duplicate_ids_throw_naming_the_id`
  - `Json_is_camelCase_and_omits_nulls`, an exact JSON string for a small screen
- [ ] **Step 3: Run the tests.** Expected: they fail.
- [ ] **Step 4: Implement `Surface`, the descriptions and `ScreenDescriber`.**
- [ ] **Step 5: Run the tests.** Expected: they pass. `dotnet build -c Release` gives 0 warnings.
- [ ] **Step 6: Commit.** Message:
  `feat(surface): control metadata and a generic screen describer`.

### Task 3: The action executor

**Files:**
- **Create:**
  - `src/Workshop.Surface/{SurfaceAction.cs, ActionResult.cs, IScreenNavigator.cs, SurfaceFeedback.cs, ActionExecutor.cs}`
  - `tests/Workshop.Surface.Tests/ActionExecutorTests.cs`

**Interfaces:**
- **Produces the action type:**
  `SurfaceAction(string Type, string? Screen, string? Field, string? Value, string? List, string? Row, string? Button)`.
  `Type` is one of `open`, `set`, `select` or `press`, and the JSON uses those field names.
- **Produces the result:**
  `ActionResult(string Outcome, string? Message, ScreenDescription Screen)`. `Outcome` is in
  `Outcomes`, which has `Ok = "ok"`, `ValidationFailed = "validation_failed"`,
  `NotFound = "not_found"` and `Disabled = "disabled"`.
- **Produces the navigator:**
  `IScreenNavigator { IReadOnlyList<(string Id, string Title)> Screens { get; } Control Current { get; } bool Open(string id); }`
- **Produces the feedback channel:**
  `SurfaceFeedback.Fail(Control anyControlOnScreen, string message)`. The app's handlers call it
  when a rule refuses an action. The executor reads it and clears it after each `press`.
- **Produces the executor:** `ActionExecutor(IScreenNavigator navigator)` with
  `ActionResult Execute(SurfaceAction action)`. It must run on the UI thread; Task 4 marshals the
  call there.

**Behaviour:**
- **`open`:**
  - an unknown screen gives `not_found`
  - otherwise `navigator.Open`
- **`set`:**
  - an unknown field gives `not_found`
  - a disabled field gives `disabled`
  - **parse failures give `validation_failed`, with these exact messages:**
    - number: `"<label> needs a whole number."`
    - outside the `NumericUpDown`'s minimum and maximum:
      `"<label> must be between <min> and <max>."`
    - a choice not among the options: `"<label> must be one of: <a>, <b>, …"`
    - a date that isn't `yyyy-MM-dd`: `"<label> needs a date as yyyy-MM-dd."`
    - longer than `MaxLength`: `"<label> can be at most <n> characters."`
    - a checkbox value other than `true` or `false`: `"<label> needs true or false."`
  - otherwise it sets the value on the control, which raises its normal change events
- **`select`:**
  - an unknown list or row gives `not_found`
  - otherwise it selects that row only, and focuses it
- **`press`:**
  - an unknown button gives `not_found`
  - a disabled button gives `disabled`
  - otherwise it calls `PerformClick()`
  - if the handler called `SurfaceFeedback.Fail`, the result is `validation_failed` with that
    message; otherwise `ok`
- **Every result** carries `Describe(navigator.Current)` after the action.

- [ ] **Step 1: Write `ActionExecutorTests`.** They use a two-screen test navigator built from
  test forms. The tests:
  - `Open_switches_screen_and_returns_its_description`
  - `Open_unknown_is_not_found`
  - `Set_text_number_choice_checkbox_date_round_trip`
  - **one test per parse failure**, asserting the exact message above. These are Review Focus 3.
  - `Set_disabled_is_disabled`
  - `Select_row_selects_only_that_row`
  - `Press_runs_the_handler`
  - `Press_with_feedback_is_validation_failed_with_its_message`
  - `Press_disabled_is_disabled`
  - `Feedback_is_cleared_after_each_press`
- [ ] **Step 2: Run the tests.** Expected: they fail.
- [ ] **Step 3: Implement the executor and its types.**
- [ ] **Step 4: Run the tests.** Expected: they pass, with 0 warnings.
- [ ] **Step 5: Commit.** Message:
  `feat(surface): execute open, set, select and press through the real controls`.

### Task 4: The endpoint, the session file and the audit log

**Files:**
- **Create:**
  - `src/Workshop.Surface/{SurfaceEndpoint.cs, SessionFile.cs, AuditLog.cs}`
  - `tests/Workshop.Surface.Tests/{SurfaceEndpointTests.cs, SessionFileTests.cs, AuditLogTests.cs}`

**Interfaces:**
- **Produces the session file:**
  `SessionFile.Write(string directory, int port, string token) -> string path` and
  `SessionFile.Read(string directory) -> SessionInfo(int Port, string Token, int Pid, DateTimeOffset StartedAt)`.
  - **The file** is `session.json` in the directory.
  - **The ACL** grants the current Windows user `FullControl`, with inheritance removed and no
    other entries.
  - **The default directory** is `%LOCALAPPDATA%\FoundryWorkshopAgent\`. Tests pass a temporary
    directory.
- **Produces the audit log:**
  `AuditLog(string path)`, with `Append(SurfaceAction action, string outcome)`. It writes one JSON
  line: `{"at":…,"type":…,"target":…,"value":…,"outcome":…}`. `target` is the screen, field,
  list and row, or button, as relevant.
- **Produces the endpoint:**
  `SurfaceEndpoint(IPAddress address, int port, string sessionDirectory, Control uiAnchor, IScreenNavigator navigator, AuditLog audit)`,
  with `Start()`, `Stop()` and `IDisposable`. `uiAnchor` is a control whose handle exists, used
  to `Invoke` onto the UI thread. The default port is `47811`.
  - **Its routes:**

    | Route | Response |
    |---|---|
    | `GET /screens` | `[{"id","title"}]` |
    | `GET /screen` | `ScreenDescription` |
    | `POST /actions` | `ActionResult` |

**Behaviour, in check order:**
1. **The address.** The constructor throws `ArgumentException` unless the address is
   `IPAddress.Loopback` (127.0.0.1). The listener prefix is `http://127.0.0.1:<port>/`.
2. **Start-up.** `Start()` binds first and writes the session file only after the bind
   succeeds. So a second instance on the same port throws an `InvalidOperationException` naming
   the port, and leaves the existing session file untouched. A fresh 32-byte random token,
   base64url, is generated for every `Start()`.
3. **The `Host` header.** A `Host` other than `127.0.0.1:<port>` or `localhost:<port>` gives
   `403`, with `{"error":"bad_host"}`.
4. **The token.** A missing or wrong `X-Surface-Token` gives `401`, with
   `{"error":"unauthorized"}`. The comparison uses
   `CryptographicOperations.FixedTimeEquals`.
5. **The method and route.**
   - `OPTIONS` or any other method on a known route gives `405`.
   - An unknown route gives `404`.
   - No response ever carries an `Access-Control-*` header.
6. **The `POST` body.**
   - a `Content-Type` other than `application/json` gives `415`
   - a body over 65,536 bytes gives `413`
   - invalid JSON, or an unknown `type`, gives `400`, with `{"error":"bad_request"}`
7. **Execution.** Each request is executed through `uiAnchor.Invoke`, behind a
   `SemaphoreSlim(1)`, so actions are serialised (Review Focus 4). The executor or describer is
   wrapped in a 10-second timeout. On a timeout the endpoint answers `503`, with
   `{"error":"ui_timeout"}`, and the listener carries on (Review Focus 1).
8. **Auditing.** Every `POST /actions` that reaches the executor is audited, whatever its
   outcome.
9. **Shutdown.** `Stop()` deletes the session file only if it still holds this instance's token.

- [ ] **Step 1: Write the failing tests.** They use a real `HttpClient` against an endpoint on a
  test port, with a test navigator on an STA UI thread.
  - **Start-up and binding:**
    - `Refuses_a_non_loopback_address`
    - `Second_instance_fails_and_leaves_the_session_file`
    - `Each_start_writes_a_new_token`
    - `Stale_session_file_is_replaced`
  - **Requests that are refused:**
    - `Missing_or_wrong_token_is_401`
    - `Foreign_host_header_is_403`, sending a `Host: evil.example:47811` request
    - `Options_is_405_without_cors_headers`
    - `Unknown_route_is_404`
    - `Wrong_content_type_is_415`
    - `Oversize_body_is_413`
    - `Bad_json_is_400`
  - **Requests that are served:**
    - `Get_screen_and_screens_with_token`
    - `Post_action_returns_the_result_and_audits_it`
    - `Parallel_posts_are_serialised`
    - `Blocked_ui_thread_is_503_and_the_endpoint_recovers`, using a test handler that sleeps for
      15 seconds
    - `Stop_removes_only_its_own_session_file`
  - `SessionFileTests.Acl_grants_only_the_current_user`
- [ ] **Step 2: Run the tests.** Expected: they fail.
- [ ] **Step 3: Implement the endpoint, the session file and the audit log.** Use
  `HttpListener.GetContextAsync` in a loop on a background task.
- [ ] **Step 4: Run the tests.** Expected: they pass, with 0 warnings.
  - **If `http://127.0.0.1:<port>/` needs a URL ACL** without admin rights on Windows, stop and
    report NEEDS_CONTEXT with the exact error. Do not register a URL ACL and do not run elevated.
    The controller will rule between `localhost` plus the remote-endpoint check, and a different
    listener.
- [ ] **Step 5: Commit.** Message:
  `feat(surface): a loopback-only, token-guarded endpoint with a session file and an audit log`.

### Task 5: The workshop app and its end-to-end tests

**Files:**
- **Create:**
  - `src/Workshop.App/{Program.cs, MainForm.cs, WorkshopNavigator.cs}`
  - `src/Workshop.App/Screens/{JobListScreen, JobDetailScreen, NewJobScreen, CustomerListScreen, CustomerDetailScreen, PartsScreen}.cs`
  - `tests/Workshop.App.Tests/{MetadataCoverageTests.cs, EndToEndTests.cs, AppHost.cs}`
- **Modify:** `README.md`

**Interfaces:**
- **Consumes** Core's `JobService` and `WorkshopDb` (Task 1), and Surface's `Surface.Meta`,
  `Surface.Screen`, `SurfaceFeedback`, `IScreenNavigator`, `SurfaceEndpoint` and `AuditLog`
  (Tasks 2–4).
- **Produces the command line:** `Workshop.App.exe [--db <path>] [--port <n>] [--session-dir <dir>]`.
  - **Without `--db`,** it creates a fresh database at
    `%LOCALAPPDATA%\FoundryWorkshopAgent\workshop-<timestamp>.db`.
  - **With `--db <path>`,** the file must already exist. Plan 2's runner creates a fresh copy and
    passes it.
  - **The audit log** is `audit.jsonl` beside the database.
- **Produces the screen IDs** used by plan 2's scenarios:

| Screen | Its controls |
|---|---|
| `job-list` | `search` text, `status-filter` choice (`all` + the 7 statuses), `jobs` list (columns `Job`, `Customer`, `Device`, `Status`), `open-job` and `new-job` buttons |
| `job-detail` | `job` text, read-only and disabled; `status` choice; `fault` text, disabled; `parts` list; `part` choice; `quantity` number (1–20); `add-part`, `fit-ordered-parts`, `save-status` buttons; `note` text (max 1,000); `add-note` button; `notes` list; `cancel-job` button, destructive |
| `new-job` | `customer` choice; `device` choice, filtered by customer; `fault` text (max 500); `book-in` button |
| `customer-list` | `search` text, `customers` list, `open-customer` button |
| `customer-detail` | `name`, `phone`, disabled; `devices` list; `kind` choice (laptop, desktop, phone, tablet, printer, other); `model` text; `serial` text; `add-device` button |
| `parts` | `parts` list (`Part`, `Name`, `Stock`); `quantity` number; `receive-stock` button |

**Behaviour:**
- **Opening a job.** `open-job` and `open-customer` open the selected row's record. With no row
  selected, they fail with `"Select a job first."` or `"Select a customer first."`.
- **Navigation.** `MainForm` hosts one screen at a time. `WorkshopNavigator` implements
  `IScreenNavigator`, and opening a detail screen through `open` without a selected record opens
  the list instead.
- **Rule failures.** Every `RuleResult.Fail` is passed to `SurfaceFeedback.Fail`, and shown in an
  inline error label. There is no `MessageBox` anywhere in `Workshop.App`.
- **Messages after success.** A successful `AddPart` that went on order shows its message
  inline, with the outcome `ok`.

- [ ] **Step 1: Write `MetadataCoverageTests`.** These cover Review Focus 5.
  - `Every_interactive_control_on_every_screen_has_metadata`: it walks every screen's controls,
    and every `TextBox`, `ComboBox`, `NumericUpDown`, `CheckBox`, `DateTimePicker`, `Button` and
    `ListView` must have `Surface.MetaOf(c) != null`.
  - `Every_screen_describes_without_throwing_and_ids_match_the_table`
  - `No_MessageBox_in_Workshop_App`: a source scan of `src/Workshop.App` for `MessageBox.`
- [ ] **Step 2: Write `EndToEndTests`.** `AppHost` starts `MainForm` on an STA thread with a
  fresh temporary database, a test port and a temporary session directory, then drives the app
  through HTTP. The tests:
  - `Book_in_Sam_Riveras_laptop`: `open` `new-job`, `set` `customer`, `set` `device`, `set`
    `fault`, `press` `book-in`. Assert that the database has a new `booked in` job for that
    device.
  - `Ready_with_a_part_on_order_is_validation_failed`: assert the exact message from Task 1.
  - `Cancel_job_button_is_described_as_destructive`
  - `Find_the_Henderson_printer_job_status`: a look-up through `search` and `jobs`. Its status is
    `diagnosing`.
  - `Audit_log_has_one_line_per_action`
- [ ] **Step 3: Run the tests.** Expected: they fail.
- [ ] **Step 4: Implement the screens, the navigator, `MainForm` and `Program`.**
- [ ] **Step 5: Run the full suite with `dotnet build -c Release` and `dotnet test -c Release`.**
  Expected: 0 warnings, and every test passes.
- [ ] **Step 6: Run the app once and check it by hand, then complete the README.**
  - **Check:** run `dotnet run --project src/Workshop.App`, confirm `session.json` appears, and
    call `GET /screen` with the token from PowerShell.
  - **README:** what the repo is (one paragraph, pointing to the spec); how to run the app; the
    endpoint's API and the security model in a short list (loopback only, a per-launch token in
    a user-only file, `Host` check, no CORS, serialised UI actions, audit log); and the status:
    plan 1 of 3.
- [ ] **Step 7: Commit.** Message:
  `feat(app): the workshop app's screens, wired to the surface, with end-to-end tests`.
