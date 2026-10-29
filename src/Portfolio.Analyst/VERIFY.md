# What plan 2's MCP spike must verify

The analyst is built and tested offline only: no call has reached Foundry or Fabric. These are the
points the code assumes about the live service, each with what the code does today.

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

## 2. The service reads the definition back as it was sent

`AnalystAgent.EnsureAsync` reuses the agent's latest version only when its raw JSON holds exactly the
code's definition. It also refuses a run when the version read back differs: the five tool fields
above, the model and the instructions are compared, and any other field must be at its default.

**Verify:** the read-back echoes `server_url`, `project_connection_id` and `require_approval`
unchanged (not normalised, not dropped, not moved under another key). If the service drops or
rewrites one, the drift check will refuse every run, naming that field. Adjust the comparison to the
service's shape, not the other way round.

## 3. The answer's shape

A canned Responses result (`mcp_list_tools`, `mcp_call`, then a `message`) maps through
Microsoft.Extensions.AI.OpenAI 10.6.0 to `McpServerToolCallContent`, `McpServerToolResultContent` and
`TextContent`. Each `McpServerToolCallContent` counts as one tool call; `usage` gives the tokens.

**Verify against a live answer:**
- The output items are as above.
- No `mcp_approval_request` arrives with `require_approval: never`. An approval request would leave the
  answer empty, and the run would record that empty answer.
- `usage.input_tokens` counts the tool's output that the model read.

## 4. Failures

A failed call stops the run with exit 3 and leaves the `.jsonl.partial` file. Failures include an
HTTP error, a content-filter refusal mapped by `SdkErrors`, and a function call left to the client.
The run never scores the failure as a wrong answer. The console shows only the HTTP status and a
type or a code.

**Verify** what an MCP-side failure looks like: the data agent erroring or timing out, or the
connection lacking access. It may arrive as an HTTP error, or as an `mcp_call` item with `error`
set and a normal message after it. The second case would be recorded as an answer.
