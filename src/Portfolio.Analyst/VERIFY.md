# What plan 2's MCP spike must verify

The analyst was built and tested offline. These are the points the code assumed about the live
service, each with what the code does. Each now ends with what the live run found: the Fabric repo's
capstone, plan 2, step 10 (the MCP spike, 16 November 2026) and step 14 (the measured passes,
17 November 2026), run at this repo's `51bf705` with `gpt-5.5`.

## 1. The project connection goes in through an untyped patch

Azure.AI.Projects 2.0.1 (with Azure.AI.Extensions.OpenAI 2.0.0 and OpenAI 2.10.0 under it) builds a
prompt agent's MCP tool from the OpenAI SDK's `OpenAI.Responses.McpTool`. Checked by reflection over
the restored assemblies on 2026-10-30, `McpTool` has:

- `ServerLabel`, `ServerUri` and `ServerDescription`;
- `ConnectorId` (OpenAI's own connectors only: Dropbox, Gmail, SharePoint and so on);
- `AuthorizationToken` and `Headers`;
- `AllowedTools` and `ToolCallApprovalPolicy` (`GlobalMcpToolCallApprovalPolicy.NeverRequireApproval`
  gives `"require_approval": "never"`).

It has **no typed project connection**, and neither Azure package adds an MCP tool type or an
extension method for one. The code sets it through the model's `Patch` (`JsonPatch`, marked
experimental as `SCME0001`):

```csharp
tool.Patch.Set("$.project_connection_id"u8, setup.ConnectionId);
```

The agent definition the SDK then sends is:

```json
{"kind":"prompt","model":"<model>","instructions":"<instructions.md>",
 "tools":[{"type":"mcp","server_label":"portfolio-data","server_url":"<ANALYST_MCP_URL>",
           "require_approval":"never","project_connection_id":"<ANALYST_CONNECTION_ID>"}]}
```

**Verify:**
- Foundry accepts `project_connection_id` on an `mcp` tool, by that name.
- It signs the MCP calls in through the connection, with no header or token from the client.
- Which connection kind and which value the field wants: the connection's name or its full resource ID.

If a later SDK adds a typed member, move to it only when this repo's pins move (Directory.Packages.props
explains why OpenAI stays at 2.10.0).

**Checked live (16 November 2026).** Foundry accepted `project_connection_id` on the `mcp` tool, by
that name, and signed the MCP calls in through the connection, with no header or token from the
client. The connection was a project connection of category `RemoteTool`, auth type
`UserEntraToken` (user-delegated, so `isSharedToAll` stays false), with audience
`https://analysis.windows.net/powerbi/api` and the data agent's MCP endpoint as its target. The field
took either value: the connection's name and its full resource ID both worked. The measured runs
used the name.

## 2. The service reads the definition back as it was sent

`AnalystAgent.EnsureAsync` reuses the agent's latest version only when its raw JSON holds exactly the
code's definition. It also refuses a run when the version read back differs: the five tool fields
above, the model and the instructions are compared, and any other field must be at its default.

**Verify:** the read-back echoes `server_url`, `project_connection_id` and `require_approval`
unchanged (not normalised, not dropped, not moved under another key). If the service drops or
rewrites one, the drift check will refuse every run, naming that field. Adjust the comparison to the
service's shape, not the other way round.

**Checked live (16–17 November 2026).** The drift check passed on every run, so the read-back echoed
the fields unchanged. The comparison needed no change.

## 3. The answer's shape

A canned Responses result (`mcp_list_tools`, `mcp_call`, then a `message`) maps through
Microsoft.Extensions.AI.OpenAI 10.6.0 to `McpServerToolCallContent`, `McpServerToolResultContent` and
`TextContent`. Each `McpServerToolCallContent` counts as one tool call; `usage` gives the tokens.

**Verify against a live answer:**
- The output items are as above.
- No `mcp_approval_request` arrives with `require_approval: never`. If one arrives (Microsoft.Extensions.AI
  maps it to `ToolApprovalRequestContent`), the run stops with code `approval_request`.
- `usage.input_tokens` counts the tool's output that the model read.

**Checked live (16–17 November 2026).** The output items were a reasoning item, `mcp_call` (server
label `portfolio-data`, tool `DataAgent_da_portfolio`, one argument `userQuestion`), `mcp_result`
(the data agent's JSON answer) and the text. No approval request arrived in 2 spike answers and 83
measured ones. `usage` came with every answer. `input_tokens` was about 1,200 with one tool call and
1,940 to 2,018 with two, so the tool's output is counted.

## 4. Failures

A failed call stops the run with exit 3 and leaves the `.jsonl.partial` file. Failures include an
HTTP error, a content-filter refusal mapped by `SdkErrors`, a function call left to the client
(`tool_calls`), an MCP call whose result is an error (`tool_error`, as the direct route stops on an
MCP `isError`) and an approval request (`approval_request`). The failed question gets no record.
The run never scores the failure as a wrong answer. The console shows only the HTTP status and a
type or a code.

**Verify** what an MCP-side failure looks like: the data agent erroring or timing out, or the
connection lacking access. It may arrive as an HTTP error, or as an `mcp_call` item with `error`
set and a normal message after it. The second case stops the run with `tool_error` (checked against a
canned body, where `error` maps to an `ErrorContent` output); confirm the live service sets `error`.

**Partly checked live (17 November 2026).** An HTTP error behaves as described. The first measured
attempt stopped in pass 3 on `HTTP 500, ClientResultException` from Foundry: exit 3, the
`.jsonl.partial` file left with the 38 records already written, and no record for the failed
question. The console showed only the status and the type. No MCP-side failure happened, so how the
live service reports one is still unverified.

`--passes` always numbers passes from 1, so a failed pass can't be re-run on its own: its records
would duplicate pass 1's. The capstone set the failed attempt aside and re-ran all three passes.
