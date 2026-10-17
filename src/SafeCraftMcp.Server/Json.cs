using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SafeCraftMcp.Server;

/// <summary>JSON helpers that need no reflection (the gateway is published with NativeAOT).</summary>
public static class Json
{
    public static string Indented(JsonNode node)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            // The text goes to an agent, not into HTML: keep apostrophes and non-ASCII readable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode?)v)]);
}

[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal sealed partial class GatewayJsonContext : JsonSerializerContext;
