# foundry-workshop-agent

A small, made-up repair-workshop app for Windows (WinForms on .NET 10), built from scratch so
that a local agent can drive it through a generic, self-describing and locked-down HTTP endpoint.
Every screen's controls carry metadata, one generic describer turns the open screen into JSON, and
the agent acts only through the real controls, as a person would. Later plans add an agent client
that runs the same agent on GPT and on Claude in Microsoft Foundry, and measure it on a fixed set
of scenarios. The design is in
[docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md](docs/superpowers/specs/2026-10-04-foundry-workshop-agent-design.md).

**Status: plan 1 of 3.** The app, its storage and rules, the describer, the endpoint and its
lock-down are built and tested. The agent client and scenarios (plan 2) and the Azure study
(plan 3) are not built yet. Nothing here uses Azure, and every record in the database is made up.

## Run the app

```
dotnet run --project src/Workshop.App
```

or, once built, `Workshop.App.exe [--db <path>] [--port <n>] [--session-dir <dir>]`:

| Option | Default | Meaning |
|---|---|---|
| `--db <path>` | a fresh database, `%LOCALAPPDATA%\FoundryWorkshopAgent\workshop-<timestamp>.db` | A database file that must already exist, such as a fresh copy a test runner made. |
| `--port <n>` | `47811` | The endpoint's port on `127.0.0.1`. |
| `--session-dir <dir>` | `%LOCALAPPDATA%\FoundryWorkshopAgent` | Where `session.json` is written. |

The audit log, `audit.jsonl`, is written beside the database. If the app cannot start (the
`--db` file is missing, or another instance holds the port) it exits with code 1, and with code 2
for a bad command line; the reason goes to standard error. A second instance never touches the
first one's session file.

The screens are `job-list`, `job-detail`, `new-job`, `customer-list`, `customer-detail` and
`parts`. A person moves between them with the menu; the agent opens them by ID.

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
| `GET /screen` | the current screen: its `fields` (kind, value, options, enabled, required, maxLength), `buttons` (enabled, destructive) and `lists` (columns, and rows with a `key` to select them by) |
| `POST /actions` | one action, as JSON: `{"type":"open","screen":…}`, `{"type":"set","field":…,"value":…}`, `{"type":"select","list":…,"row":…}` or `{"type":"press","button":…}` |

An action answers its `outcome` (`ok`, `validation_failed`, `not_found` or `disabled`), the
app's own `message` when it has one (a rule's refusal, or news such as a part going on order), and
the new `screen`. A request the endpoint refuses gets an HTTP error with a body such as
`{"error":"unauthorized"}`; a UI thread busy for more than 10 seconds gets `503`
`{"error":"ui_timeout"}`.

## Security model

- **Loopback only.** The endpoint is constructed for `127.0.0.1` only and refuses any other
  address. It also holds the `localhost` name for its port, so no other process can register a
  route and capture a token sent to `localhost`. HTTP.sys accepts a `localhost` registration on
  every local address, so the listener is reachable on the machine's other addresses too: every
  request from a non-loopback address is answered `403` (`not_loopback`) before any other check.
  Windows Firewall blocks unsolicited inbound connections by default; the app adds no firewall
  rule and needs no URL ACL or administrator rights.
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
  outcome.

## Build and test

```
dotnet build -c Release
dotnet test -c Release
```

The tests include end-to-end runs of the app driven through its endpoint.

## Licence

Apache-2.0. See [LICENSE](LICENSE).
