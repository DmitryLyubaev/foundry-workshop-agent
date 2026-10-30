using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;

namespace Portfolio.Analyst.Tests;

/// <summary>One request the SDK sent, as the fake service saw it.</summary>
internal sealed record Sent(HttpMethod Method, Uri Uri, string? Authorization, string Body)
{
    public string Path => Uri.AbsolutePath;

    public JsonObject Json => JsonNode.Parse(Body)!.AsObject();
}

/// <summary>
/// The Foundry project's agent and Responses endpoints, faked under the SDK's own HTTP stack: it
/// keeps the <c>portfolio-analyst</c> agent's versions as the service stores them, and answers each
/// Responses call with <see cref="Response"/>. No request leaves the process.
/// </summary>
internal sealed class FakeFoundry : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly List<Sent> requests = [];
    private readonly List<JsonObject> versions = [];

    public FakeFoundry(params JsonObject[] definitions)
    {
        foreach (var definition in definitions)
        {
            Store(definition);
        }
    }

    /// <summary>Changes a version as it is read back, as a service-side edit would.</summary>
    public Func<JsonObject, JsonObject>? ReadBack { get; set; }

    /// <summary>The Responses API's answer to every <c>POST …/responses</c>.</summary>
    public string Response { get; set; } = ResponseWith("No answer.");

    /// <summary>Answers taken in turn before <see cref="Response"/>, one per <c>POST …/responses</c>.</summary>
    public Queue<string> Responses { get; } = new();

    public List<JsonObject> Created { get; } = [];

    public IReadOnlyList<Sent> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    public static AnalystSetup Setup { get; } = new(
        new Uri("https://example.services.ai.azure.com/api/projects/example"),
        "gpt-5.6-luna",
        new Uri("https://mcp.example.invalid/v1/workspaces/w/dataagents/d/mcp"),
        "conn-example",
        "You are the portfolio analyst.\nAnswer only from the tool.\n");

    public PipelineTransport Transport() => new HttpClientPipelineTransport(new HttpClient(this, disposeHandler: false));

    /// <summary>The definition the code should create, written here rather than by the code under test.</summary>
    public static JsonObject CodeDefinition() => new()
    {
        ["kind"] = "prompt",
        ["model"] = Setup.Model,
        ["instructions"] = Setup.Instructions,
        ["tools"] = new JsonArray(new JsonObject
        {
            ["type"] = "mcp",
            ["server_label"] = "portfolio-data",
            ["server_url"] = Setup.McpUrl.AbsoluteUri,
            ["require_approval"] = "never",
            ["project_connection_id"] = Setup.ConnectionId,
        }),
    };

    /// <summary>A Responses API result whose one MCP call failed, as the service reports it (<c>error</c> set), with an answer after it.</summary>
    public static string ToolErrorResponse() => Result(new JsonArray(
        new JsonObject
        {
            ["type"] = "mcp_call",
            ["id"] = "mcp_1",
            ["server_label"] = "portfolio-data",
            ["name"] = "ask",
            ["arguments"] = "{\"question\":\"How much?\"}",
            ["error"] = new JsonObject { ["type"] = "tool_execution_error", ["message"] = "The data agent at https://mcp.example.invalid/x failed." },
        },
        Message("The tool failed, but p2 spent about A$1,200.")));

    /// <summary>A Responses API result that asks for an approval before calling the MCP tool.</summary>
    public static string ApprovalResponse() => Result(new JsonArray(new JsonObject
    {
        ["type"] = "mcp_approval_request",
        ["id"] = "mcpr_1",
        ["server_label"] = "portfolio-data",
        ["name"] = "ask",
        ["arguments"] = "{\"question\":\"How much?\"}",
    }));

    /// <summary>A Responses API result: the data agent's tool listed and called once, then the answer.</summary>
    public static string ResponseWith(string answer, int calls = 1)
    {
        var output = new JsonArray(new JsonObject
        {
            ["type"] = "mcp_list_tools",
            ["id"] = "mcpl_1",
            ["server_label"] = "portfolio-data",
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "ask", ["input_schema"] = new JsonObject { ["type"] = "object" } }),
        });
        for (var i = 1; i <= calls; i++)
        {
            output.Add(new JsonObject
            {
                ["type"] = "mcp_call",
                ["id"] = $"mcp_{i}",
                ["server_label"] = "portfolio-data",
                ["name"] = "ask",
                ["arguments"] = "{\"question\":\"How much has p2 spent?\"}",
                ["output"] = "p2 spent A$1,234.56 (AUD).",
            });
        }

        output.Add(Message(answer));
        return Result(output);
    }

    private static JsonObject Message(string answer) => new()
    {
        ["type"] = "message",
        ["id"] = "msg_1",
        ["role"] = "assistant",
        ["status"] = "completed",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = answer, ["annotations"] = new JsonArray() }),
    };

    private static string Result(JsonArray output) =>
        new JsonObject
        {
            ["id"] = "resp_1",
            ["object"] = "response",
            ["created_at"] = 1762592400,
            ["status"] = "completed",
            ["model"] = Setup.Model,
            ["output"] = output,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 1234,
                ["output_tokens"] = 56,
                ["total_tokens"] = 1290,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
            },
            ["parallel_tool_calls"] = true,
            ["tool_choice"] = "auto",
            ["tools"] = new JsonArray(),
        }.ToJsonString();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var sent = new Sent(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body);
        lock (gate)
        {
            requests.Add(sent);
            return Answer(sent);
        }
    }

    private HttpResponseMessage Answer(Sent request)
    {
        var path = request.Path;
        const string agent = "/agents/portfolio-analyst";
        if (request.Method == HttpMethod.Post && path.EndsWith("/responses", StringComparison.Ordinal))
        {
            return Json(200, Responses.Count > 0 ? Responses.Dequeue() : Response);
        }

        if (request.Method == HttpMethod.Get && path.EndsWith(agent, StringComparison.Ordinal))
        {
            return versions.Count == 0
                ? Json(404, """{"error":{"code":"not_found","message":"Agent portfolio-analyst was not found."}}""")
                : Json(200, new JsonObject
                {
                    ["object"] = "agent",
                    ["id"] = "portfolio-analyst",
                    ["name"] = "portfolio-analyst",
                    ["versions"] = new JsonObject { ["latest"] = versions[^1].DeepClone() },
                }.ToJsonString());
        }

        if (request.Method == HttpMethod.Post && path.EndsWith($"{agent}/versions", StringComparison.Ordinal))
        {
            var definition = request.Json["definition"]!.AsObject();
            Created.Add(definition);
            return Json(200, Store(definition).ToJsonString());
        }

        var prefix = $"{agent}/versions/";
        var at = path.LastIndexOf(prefix, StringComparison.Ordinal);
        if (request.Method == HttpMethod.Get && at >= 0)
        {
            var stored = versions[int.Parse(path[(at + prefix.Length)..], CultureInfo.InvariantCulture) - 1].DeepClone().AsObject();
            return Json(200, (ReadBack?.Invoke(stored) ?? stored).ToJsonString());
        }

        return Json(500, """{"error":{"code":"unexpected","message":"The fake has no answer."}}""");
    }

    private JsonObject Store(JsonObject definition)
    {
        var version = (versions.Count + 1).ToString(CultureInfo.InvariantCulture);
        var stored = new JsonObject
        {
            ["object"] = "agent.version",
            ["id"] = $"portfolio-analyst:{version}",
            ["name"] = "portfolio-analyst",
            ["version"] = version,
            ["created_at"] = 1762592400,
            ["definition"] = definition.DeepClone(),
        };
        versions.Add(stored);
        return stored;
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

/// <summary>A credential that hands out a made-up token and keeps the scopes asked for. No sign-in happens.</summary>
internal sealed class FakeCredential : TokenCredential
{
    public List<string> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (Scopes)
        {
            Scopes.AddRange(requestContext.Scopes);
        }

        return new AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
