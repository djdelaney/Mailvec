using System.Text.Json;
using Mailvec.Mcp;

namespace Mailvec.Mcp.Tests;

public class BooleanArgumentCoercionTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "flag":     { "type": "boolean" },
            "maybe":    { "type": ["boolean", "null"] },
            "either":   { "type": ["boolean", "string"] },
            "name":     { "type": "string" },
            "limit":    { "type": ["integer", "null"] }
          }
        }
        """).RootElement;

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"TRUE\"", true)]
    [InlineData("\" False \"", false)]
    public void A_string_boolean_becomes_a_real_one_where_the_schema_says_boolean(string raw, bool expected)
    {
        var args = Args($$"""{ "flag": {{raw}}, "maybe": {{raw}} }""");

        BooleanArgumentCoercion.Coerce(args, Schema).ShouldBe(2);

        args["flag"].GetBoolean().ShouldBe(expected);
        args["maybe"].GetBoolean().ShouldBe(expected);
    }

    [Fact]
    public void Only_the_exact_words_are_accepted_anything_else_is_left_for_binding_to_reject()
    {
        var args = Args("""{ "flag": "yes", "maybe": "1" }""");

        BooleanArgumentCoercion.Coerce(args, Schema).ShouldBe(0);

        args["flag"].GetString().ShouldBe("yes");
        args["maybe"].GetString().ShouldBe("1");
    }

    [Fact]
    public void Parameters_that_are_not_boolean_or_that_also_accept_strings_are_untouched()
    {
        // "true" is a legitimate string value for a string parameter, and for
        // one whose schema allows both.
        var args = Args("""{ "name": "true", "either": "true", "limit": "3", "unknown": "true" }""");

        BooleanArgumentCoercion.Coerce(args, Schema).ShouldBe(0);

        args["name"].ValueKind.ShouldBe(JsonValueKind.String);
        args["either"].ValueKind.ShouldBe(JsonValueKind.String);
        args["limit"].ValueKind.ShouldBe(JsonValueKind.String);
        args["unknown"].ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public void Real_booleans_and_a_schema_without_properties_are_no_ops()
    {
        var args = Args("""{ "flag": true, "maybe": null }""");
        BooleanArgumentCoercion.Coerce(args, Schema).ShouldBe(0);
        args["flag"].GetBoolean().ShouldBeTrue();

        BooleanArgumentCoercion.Coerce(Args("""{ "flag": "true" }"""),
            JsonDocument.Parse("""{ "type": "object" }""").RootElement).ShouldBe(0);
    }
}
