using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.Server;

/// <summary>One call of a gateway tool: its checked arguments, the client's progress sink and cancellation.</summary>
public sealed record ToolCall(IReadOnlyDictionary<string, JsonElement> Arguments, IProgress<ProgressNotificationValue>? Progress, CancellationToken CancellationToken)
{
    public string String(string name) => Arguments[name].GetString()!;

    public string? OptionalString(string name) => Arguments.TryGetValue(name, out var value) ? value.GetString() : null;
}

/// <summary>One gateway tool: its MCP description and what runs when it's called.</summary>
public sealed record GatewayTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    /// <summary>Argument name → JSON schema of the argument.</summary>
    public IReadOnlyDictionary<string, JsonObject> Arguments { get; init; } = new Dictionary<string, JsonObject>();
    public IReadOnlyList<string> Required { get; init; } = [];

    public required bool ReadOnly { get; init; }
    public required bool Destructive { get; init; }
    public required bool Idempotent { get; init; }

    public required Func<ToolCall, Task<CallToolResult>> Run { get; init; }

    public Tool ToProtocol() => new()
    {
        Name = Name,
        Title = Title,
        Description = Description,
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(Arguments.Select(a => KeyValuePair.Create(a.Key, (JsonNode?)a.Value.DeepClone()))),
            ["required"] = new JsonArray([.. Required.Select(r => (JsonNode?)r)]),
            ["additionalProperties"] = false,
        }, GatewayJsonContext.Default.JsonObject),
        // All four hints on every tool, so each client's approval UI can tell reads from writes.
        // No gateway tool reaches the internet; the apps run without network.
        Annotations = new ToolAnnotations
        {
            Title = Title,
            ReadOnlyHint = ReadOnly,
            DestructiveHint = Destructive,
            IdempotentHint = Idempotent,
            OpenWorldHint = false,
        },
    };
}

/// <summary>
/// The gateway's tools and their dispatch. Arguments are checked strictly before a tool runs:
/// an unknown or missing argument, or one of the wrong JSON type, is a JSON-RPC -32602, as the
/// craft apps do it. A <see cref="GatewayRefusalException"/> becomes a tool error the agent can
/// read. No tool takes a setting, so nothing an agent sends can relax the sandbox.
/// </summary>
public sealed class ToolCatalog(IReadOnlyList<GatewayTool> tools)
{
    public IReadOnlyList<GatewayTool> Tools { get; } = tools;

    public ListToolsResult List() => new() { Tools = [.. Tools.Select(t => t.ToProtocol())] };

    public async Task<CallToolResult> CallAsync(
        string name, IDictionary<string, JsonElement>? arguments, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name)
            ?? throw new McpProtocolException($"Unknown tool '{name}'.", McpErrorCode.InvalidParams);
        var args = new Dictionary<string, JsonElement>(arguments ?? new Dictionary<string, JsonElement>());
        if (args.Keys.FirstOrDefault(k => !tool.Arguments.ContainsKey(k)) is { } unknown)
        {
            var allowed = tool.Arguments.Count == 0 ? "none" : string.Join(", ", tool.Arguments.Keys);
            throw new McpProtocolException($"{name}: unknown argument '{unknown}'. Allowed: {allowed}.", McpErrorCode.InvalidParams);
        }
        if (tool.Required.FirstOrDefault(r => !args.ContainsKey(r)) is { } missing)
            throw new McpProtocolException($"{name}: missing argument '{missing}'.", McpErrorCode.InvalidParams);
        foreach (var (key, value) in args)
        {
            var expected = (string?)tool.Arguments[key]["type"];
            var matches = expected switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "object" => value.ValueKind == JsonValueKind.Object,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => true,
            };
            if (!matches)
                throw new McpProtocolException($"{name}: argument '{key}' must be a JSON {expected}.", McpErrorCode.InvalidParams);
        }
        try
        {
            return await tool.Run(new ToolCall(args, progress, cancellationToken));
        }
        catch (GatewayRefusalException ex)
        {
            return Failure(ex.Message);
        }
    }

    /// <summary>
    /// What's wrong with a <c>tools/call</c>'s params that the SDK would fail to deserialize, or null.
    /// The SDK answers those with -32603 before <see cref="CallAsync"/> runs, so the gateway checks
    /// the raw params first and answers -32602 itself.
    /// </summary>
    public static string? MalformedCall(JsonNode? parameters)
    {
        if (parameters is not JsonObject call)
            return "tools/call: 'params' must be an object.";
        if (call["name"] is not JsonValue name || name.GetValueKind() != JsonValueKind.String)
            return "tools/call: 'name' must be a string.";
        if (call["arguments"] is { } arguments && arguments.GetValueKind() != JsonValueKind.Object)
            return "tools/call: 'arguments' must be an object.";
        return null;
    }

    public static CallToolResult Text(JsonNode json) => new()
    {
        Content = [new TextContentBlock { Text = Json.Indented(json) }],
        IsError = false,
    };

    public static CallToolResult Failure(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };
}
