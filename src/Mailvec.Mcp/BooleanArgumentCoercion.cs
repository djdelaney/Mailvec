using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp;

/// <summary>
/// Turns a string-encoded boolean argument (<c>"true"</c> / <c>"false"</c>)
/// into a real JSON boolean before the SDK binds it — but only where the
/// tool's OWN advertised input schema says that parameter is a boolean.
///
/// <para><b>Why.</b> Models routinely send <c>"true"</c> where the schema says
/// boolean, exactly as they send <c>"3"</c> where it says integer. The SDK
/// already reads numbers from strings (its default options set
/// <c>JsonNumberHandling.AllowReadingFromString</c>), but System.Text.Json has
/// no equivalent for booleans, so <c>hasAttachments: "false"</c> threw inside
/// binding and the client saw only "An error occurred invoking
/// 'search_emails'". It surfaced on the 2026-10-24 deploy: a connector session
/// that predated the release still held the old schema without <c>unread</c>,
/// didn't know its type, and sent it as a string.</para>
///
/// <para><b>Why a call-tool filter and not a JSON converter.</b> A lenient
/// <c>bool</c> converter passed to <c>WithTools</c> was tried first and made the
/// schema exporter drop <c>"type": "boolean"</c> from every boolean parameter
/// (custom converters are opaque to it) — advertising "anything" is worse than
/// the bug. This filter runs after the tool is matched and before binding, and
/// leaves registration, and therefore every advertised schema, untouched.
/// Pinned by <c>McpSurfaceTests.Boolean_parameters_still_advertise_type_boolean</c>.</para>
///
/// <para>Deliberately narrow: only the exact strings <c>true</c>/<c>false</c>
/// (case-insensitive, trimmed), never <c>"1"</c>/<c>"yes"</c>. Anything else
/// is left for binding to reject, because it is a real client mistake.</para>
/// </summary>
internal static class BooleanArgumentCoercion
{
    private static readonly JsonElement True = JsonDocument.Parse("true").RootElement.Clone();
    private static readonly JsonElement False = JsonDocument.Parse("false").RootElement.Clone();

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is McpServerTool tool && context.Params?.Arguments is { Count: > 0 } args)
                Coerce(args, tool.ProtocolTool.InputSchema);
            return next(context, cancellationToken);
        };

    /// <summary>Rewrites, in place, every string argument the schema declares boolean.</summary>
    internal static int Coerce(IDictionary<string, JsonElement> arguments, JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return 0;

        var rewritten = 0;
        foreach (var name in arguments.Keys.ToList())
        {
            var value = arguments[name];
            if (value.ValueKind != JsonValueKind.String) continue;
            if (!properties.TryGetProperty(name, out var schema) || !DeclaresBoolean(schema)) continue;

            var s = value.GetString()?.Trim();
            if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) arguments[name] = True;
            else if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) arguments[name] = False;
            else continue;
            rewritten++;
        }
        return rewritten;
    }

    // `"type": "boolean"` or `"type": ["boolean", "null"]` — the two shapes the
    // exporter emits for bool and bool?. A schema that ALSO allows "string" is
    // left alone: there the string is a legitimate value, not an encoding slip.
    private static bool DeclaresBoolean(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type)) return false;
        if (type.ValueKind == JsonValueKind.String) return type.GetString() == "boolean";
        if (type.ValueKind != JsonValueKind.Array) return false;
        var types = type.EnumerateArray().Select(t => t.GetString()).ToList();
        return types.Contains("boolean") && !types.Contains("string");
    }
}
