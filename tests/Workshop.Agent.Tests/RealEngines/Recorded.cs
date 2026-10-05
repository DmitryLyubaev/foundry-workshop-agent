using System.Text.Json;

namespace Workshop.Agent.Tests.RealEngines;

/// <summary>
/// The JSON the model services answer with, in the shapes the APIs document and the SDKs parse:
/// the Foundry project's Responses API (an agent's answers), the Anthropic Messages API on Foundry,
/// and both services' errors. Made up, with placeholder names: no request was ever sent to a service.
/// </summary>
internal static class Recorded
{
    /// <summary>A Responses API answer asking for one function call.</summary>
    public static string GptCall(string responseId, string name, string arguments = "{}") => Fill("""
        {"id":"@0@","object":"response","created_at":1759600000,"status":"completed","model":"gpt-5.6-luna",
         "output":[{"type":"function_call","id":"fc_@0@","call_id":"call_@0@","name":"@1@","arguments":@2@,"status":"completed"}],
         "usage":{"input_tokens":812,"output_tokens":24,"total_tokens":836},
         "agent_reference":{"type":"agent_reference","name":"fwa-workshop-agent","version":"3"}}
        """, responseId, name, JsonSerializer.Serialize(arguments));

    /// <summary>A Responses API answer with the model's reply.</summary>
    public static string GptReply(string responseId, string text) => Fill("""
        {"id":"@0@","object":"response","created_at":1759600000,"status":"completed","model":"gpt-5.6-luna",
         "output":[{"type":"message","id":"msg_@0@","role":"assistant","status":"completed","content":[{"type":"output_text","text":"@1@","annotations":[]}]}],
         "usage":{"input_tokens":900,"output_tokens":12,"total_tokens":912}}
        """, responseId, text);

    /// <summary>A Responses API answer cut off at <c>max_output_tokens</c>, with the text written so far.</summary>
    public static string GptCutOff(string responseId, string partialText) => Fill("""
        {"id":"@0@","object":"response","created_at":1759600000,"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"model":"gpt-5.6-luna",
         "output":[{"type":"message","id":"msg_@0@","role":"assistant","status":"incomplete","content":[{"type":"output_text","text":"@1@","annotations":[]}]}],
         "usage":{"input_tokens":900,"output_tokens":4096,"total_tokens":4996}}
        """, responseId, partialText);

    /// <summary>A Responses API answer the content filter stopped.</summary>
    public static string GptFiltered(string responseId) => Fill("""
        {"id":"@0@","object":"response","created_at":1759600000,"status":"incomplete","incomplete_details":{"reason":"content_filter"},"model":"gpt-5.6-luna",
         "output":[],"usage":{"input_tokens":900,"output_tokens":0,"total_tokens":900}}
        """, responseId);

    /// <summary>An Azure error body.</summary>
    public static string AzureError(string code, string message) => Fill("""{"error":{"code":"@0@","message":"@1@"}}""", code, message);

    /// <summary>The 400 Azure answers when its content filter blocks the prompt.</summary>
    public const string AzureContentFilter = """
        {"error":{"message":"The response was filtered due to the prompt triggering Azure OpenAI's content management policy.","type":null,"param":"prompt","code":"content_filter","status":400,
         "innererror":{"code":"ResponsibleAIPolicyViolation","content_filter_result":{"hate":{"filtered":true,"severity":"high"},"violence":{"filtered":false,"severity":"safe"}}}}}
        """;

    /// <summary>A 400 for a request the Responses API cannot take.</summary>
    public const string AzureInvalidRequest = """
        {"error":{"message":"Invalid value for 'input'.","type":"invalid_request_error","param":"input","code":"invalid_value"}}
        """;

    /// <summary>A Messages API answer asking for one tool call.</summary>
    public static string ClaudeCall(string id, string name, string input = "{}") => Fill("""
        {"id":"@0@","type":"message","role":"assistant","model":"claude-haiku-4-5",
         "content":[{"type":"tool_use","id":"toolu_@0@","name":"@1@","input":@2@}],
         "stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":950,"output_tokens":31}}
        """, id, name, input);

    /// <summary>A Messages API answer with the model's reply.</summary>
    public static string ClaudeReply(string id, string text) => Fill("""
        {"id":"@0@","type":"message","role":"assistant","model":"claude-haiku-4-5",
         "content":[{"type":"text","text":"@1@"}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":990,"output_tokens":14}}
        """, id, text);

    /// <summary>A Messages API answer cut off at <c>max_tokens</c>.</summary>
    public static string ClaudeCutOff(string id, string partialText) => Fill("""
        {"id":"@0@","type":"message","role":"assistant","model":"claude-haiku-4-5",
         "content":[{"type":"text","text":"@1@"}],
         "stop_reason":"max_tokens","stop_sequence":null,"usage":{"input_tokens":990,"output_tokens":4096}}
        """, id, partialText);

    /// <summary>An Anthropic error body.</summary>
    public static string AnthropicError(string type, string message) => Fill("""{"type":"error","error":{"type":"@0@","message":"@1@"}}""", type, message);

    /// <summary>Fills a template's <c>@0@</c>, <c>@1@</c> and so on with <paramref name="values"/>.</summary>
    private static string Fill(string template, params string[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            template = template.Replace($"@{i}@", values[i], StringComparison.Ordinal);
        }

        return template;
    }
}
