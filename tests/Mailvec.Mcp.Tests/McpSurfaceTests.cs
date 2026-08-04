using System.Text.Json;
using Mailvec.Core.Data;
using Mailvec.Core.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Wire-level tests for the MCP surface: the real server driven through the
/// SDK's own <see cref="McpClient"/> over WebApplicationFactory's in-process
/// handler (no port, no child process), so <c>tools/list</c> and
/// <c>tools/call</c> travel the same JSON-RPC path Claude uses.
///
/// **Why these exist at all.** Every other MCP test in this project constructs
/// a tool class directly and calls the C# method, which skips everything the
/// SDK does between the JSON and the method: input-schema generation, argument
/// binding, error mapping, content serialization, and tool registration. The
/// locked names in CLAUDE.md "MCP API stability" are a client-facing contract
/// with no other enforcement — a parameter rename compiles, refactors the
/// direct tests' named arguments in lockstep, and breaks every client silently.
///
/// **Scope discipline.** This file tests the *envelope* — names, schemas,
/// binding, error shape, registration, identity. It deliberately does NOT test
/// search semantics, ranking, or filter behaviour: those live in the direct
/// tool tests and in Core, and re-running them through JSON-RPC would double
/// the maintenance for no new signal.
/// </summary>
public class McpSurfaceTests : IClassFixture<MailvecMcpFactory>
{
    private readonly MailvecMcpFactory _factory;

    public McpSurfaceTests(MailvecMcpFactory factory) => _factory = factory;

    // ---------- locked contract tables (CLAUDE.md "MCP API stability") ----------

    /// <summary>
    /// Per-tool: the parameter names clients pass, and the exact set the server
    /// declares required. Both halves are breaking-change surfaces — a rename
    /// breaks calls that use the old name, and newly requiring a parameter
    /// breaks every call that omits it.
    /// </summary>
    public static TheoryData<string, string[], string[]> LockedInputSchemas() => new()
    {
        { "search_emails", ["query", "mode", "limit", "folder", "dateFrom", "dateTo", "fromContains", "fromExact", "hasAttachments", "attachmentType"], [] },
        { "get_email", ["id", "messageId", "includeHtml"], [] },
        { "get_thread", ["id", "messageId", "includeBodies"], [] },
        { "list_folders", [], [] },
        { "view_attachment", ["id", "messageId", "partIndex"], ["partIndex"] },
        { "get_attachment_text", ["id", "messageId", "partIndex", "maxChars", "offset"], ["partIndex"] },
        { "get_attachment_page_image", ["id", "messageId", "partIndex", "page"], ["partIndex"] },
    };

    [Theory]
    [MemberData(nameof(LockedInputSchemas))]
    public async Task Locked_parameter_names_and_required_sets_reach_clients(
        string toolName, string[] lockedParams, string[] requiredParams)
    {
        await using var client = await ConnectAsync();

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == toolName);
        var schema = tool.JsonSchema;

        foreach (var name in lockedParams)
        {
            Properties(schema).TryGetProperty(name, out _).ShouldBeTrue(
                $"{toolName} must expose parameter '{name}' — it's a locked client contract");
        }

        // Exact set equality on `required`: additive optional parameters are
        // safe and shouldn't fail this test, but promoting one to required is a
        // breaking change that must be a deliberate version bump.
        Required(schema).ShouldBe(requiredParams, ignoreOrder: true);
    }

    [Fact]
    public async Task Wire_exposes_exactly_the_locked_tool_surface()
    {
        // ToolSurfaceTests pins the [McpServerTool(Name=...)] attributes by
        // reflection; this pins what actually reaches a client after SDK
        // registration, which is the thing clients key their configs off.
        await using var client = await ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        names.ShouldBe(ToolSurface.All.Keys, ignoreOrder: true);
    }

    [Fact]
    public async Task Every_tool_and_parameter_carries_a_description()
    {
        // Descriptions are not documentation here — they're the model's only
        // instructions for when and how to call a tool. A dropped [Description]
        // degrades retrieval quality with nothing failing anywhere.
        await using var client = await ConnectAsync();

        foreach (var tool in await client.ListToolsAsync())
        {
            tool.Description.ShouldNotBeNullOrWhiteSpace($"{tool.Name} must describe itself");

            if (!tool.JsonSchema.TryGetProperty("properties", out var props)) continue;
            foreach (var prop in props.EnumerateObject())
            {
                prop.Value.TryGetProperty("description", out var desc).ShouldBeTrue(
                    $"{tool.Name}.{prop.Name} must carry a [Description]");
                desc.GetString().ShouldNotBeNullOrWhiteSpace();
            }
        }
    }

    // ---------- hostile-content framing ----------
    //
    // Everything Mailvec returns is written by whoever sent the mail, and the
    // agent holding this connector can usually send, post, or fetch something.
    // The framing that says so is the only thing standing between a crafted
    // message and the model treating it as an instruction — and it lives in
    // free text, so nothing but a test notices when an edit drops it. These are
    // wire-level for the same reason the name locks are: what matters is what
    // reaches the client, not what a [Description] constant says in source.
    //
    // Asserting on the framing's DISTINCTIVE terms, not on whole sentences —
    // the wording should stay editable without failing here. Rewriting it so
    // "untrusted" and "instructions" both vanish is the change this catches,
    // and that change is never accidental.

    [Fact]
    public async Task Server_instructions_frame_mail_as_untrusted_and_bound_outward_actions()
    {
        await using var client = await ConnectAsync();

        var instructions = client.ServerInstructions;
        instructions.ShouldNotBeNullOrWhiteSpace();

        instructions!.ShouldContain("untrusted", Case.Insensitive);
        instructions.ShouldContain("never an instruction", Case.Insensitive);
        // The half that a per-tool description can't carry: read-only bounds
        // MAILVEC, not the agent. Dropping this leaves the model thinking a
        // read-only connector is a safe one to act on.
        instructions.ShouldContain("does not bound you", Case.Insensitive);
        instructions.ShouldContain("confirmation", Case.Insensitive);
    }

    [Theory]
    [InlineData("search_emails")]
    [InlineData("get_email")]
    [InlineData("get_thread")]
    [InlineData("view_attachment")]
    [InlineData("get_attachment_text")]
    [InlineData("get_attachment_page_image")]
    public async Task Mail_bearing_tools_declare_their_output_untrusted(string toolName)
    {
        // Per-tool as well as per-session: ServerInstructions reaches the model
        // once as standing context, but a tool description is re-read at the
        // moment it decides to call — which is the moment the framing has to be
        // in front of it. list_folders is absent on purpose: folder names come
        // from the user's own account, not from senders.
        await using var client = await ConnectAsync();

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == toolName);

        tool.Description.ShouldContain("untrusted", Case.Insensitive,
            $"{toolName} returns sender-controlled content and must say so");
        tool.Description.ShouldContain("never as instructions", Case.Insensitive);
    }

    [Fact]
    public async Task Every_tool_is_annotated_read_only_and_closed_world()
    {
        // The machine-readable half of "the surface is read-only" — clients use
        // annotations to decide what needs confirmation, and a tool that gains
        // a write path while keeping ReadOnly = true tells them to stop asking.
        // So this test's real job is to fail the day a mutating tool is added
        // and the annotation isn't reconsidered.
        await using var client = await ConnectAsync();

        foreach (var tool in await client.ListToolsAsync())
        {
            var annotations = tool.ProtocolTool.Annotations;
            annotations.ShouldNotBeNull($"{tool.Name} must carry tool annotations");
            annotations.ReadOnlyHint.ShouldBe(true, $"{tool.Name} must be annotated read-only");
            annotations.OpenWorldHint.ShouldBe(false,
                $"{tool.Name} operates on the user's own mailbox — a closed domain");
        }
    }

    [Fact]
    public async Task Locked_response_field_names_survive_serialization()
    {
        // The other half of the contract: field names clients read back and
        // narrate to users. Asserted on a real tools/call body, because the
        // server advertises no outputSchema (deliberately — see
        // docs/contributing/mcpb.md on UseStructuredContent), so serialization
        // is the only place these names exist.
        //
        // What this guards, precisely: the WIRE names, not the C# names.
        // Renaming an EmailHit property breaks compilation in the direct tool
        // tests, so the compiler already covers that. What nothing else sees is
        // a change that alters the JSON while leaving C# untouched — a
        // [JsonPropertyName], a serializer naming-policy change, or an SDK
        // upgrade changing how POCOs are serialized. Verified by mutation: a
        // [JsonPropertyName("preview")] on Snippet leaves all 150 other tests
        // green and fails only here.
        await using var client = await ConnectAsync();
        Seed("resp-fields@x", body: "ramen noodles");

        var body = await CallJsonAsync(client, "search_emails", new()
        {
            ["query"] = "ramen",
            ["mode"] = "keyword",
            // Filters set so appliedFilters echoes them: null members are
            // omitted from the JSON, so an unfiltered call yields `{}`.
            ["folder"] = "INBOX",
            ["dateFrom"] = "2000-01-01",
            ["dateTo"] = "2100-01-01",
            ["fromContains"] = "alice",
            ["hasAttachments"] = false,
        });

        ShouldHaveKeys(body, "query", "mode", "count", "results", "archiveStats", "appliedFilters");
        ShouldHaveKeys(body.GetProperty("archiveStats"), "totalMessages", "oldestDate", "latestDate");
        ShouldHaveKeys(body.GetProperty("appliedFilters"),
            "folder", "dateFrom", "dateTo", "fromContains", "hasAttachments");

        var hit = body.GetProperty("results").EnumerateArray().First();
        ShouldHaveKeys(hit, "id", "messageId", "folder", "subject", "fromAddress", "fromName", "dateSent", "snippet");
    }

    [Fact]
    public async Task Webmail_link_fields_are_emitted_when_the_account_is_configured()
    {
        // webmailLink is the pre-escaped Markdown link clients render verbatim —
        // the server builds it precisely so an untrusted subject can't be
        // assembled into a spoofed link by the model. If the field stops
        // arriving, clients quietly fall back to building their own.
        using var configured = WithConfig(("Fastmail:AccountId", "u12345678"));
        await using var client = await ConnectAsync(configured);
        Seed("webmail@x", body: "gyoza dumplings");

        var body = await CallJsonAsync(client, "search_emails", new()
        {
            ["query"] = "gyoza",
            ["mode"] = "keyword",
        });

        var hit = body.GetProperty("results").EnumerateArray().First();
        ShouldHaveKeys(hit, "webmailUrl", "webmailLink");
        hit.GetProperty("webmailLink").GetString().ShouldStartWith("[");
    }

    [Fact]
    public async Task Webmail_link_fields_are_absent_when_no_account_is_configured()
    {
        // The opt-in half. Emitting a link built from an empty account id would
        // hand the user a dead URL that still looks clickable. This is also the
        // test that proves the positive case above isn't passing on the
        // developer's own shared config — see MailvecMcpFactory.
        await using var client = await ConnectAsync();
        Seed("no-webmail@x", body: "tempura prawns");

        var body = await CallJsonAsync(client, "search_emails", new()
        {
            ["query"] = "tempura",
            ["mode"] = "keyword",
        });

        var hit = body.GetProperty("results").EnumerateArray().First();
        hit.TryGetProperty("webmailUrl", out _).ShouldBeFalse();
        hit.TryGetProperty("webmailLink", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Disabled_tool_is_absent_from_tools_list()
    {
        // docs/security.md requires dropping the two native-parser tools from
        // any internet-fronted deployment. ToolSurfaceTests proves Resolve()
        // filters the type list; this proves the client can't see the tool.
        using var trimmed = WithConfig(
            ("Mcp:DisabledTools:0", "view_attachment"),
            ("Mcp:DisabledTools:1", "get_attachment_page_image"));
        await using var client = await ConnectAsync(trimmed);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        names.ShouldNotContain("view_attachment");
        names.ShouldNotContain("get_attachment_page_image");
        names.ShouldContain("get_attachment_text"); // the pure-DB read stays
        names.Count.ShouldBe(ToolSurface.All.Count - 2);
    }

    [Fact]
    public async Task Disabled_tool_call_is_rejected_by_the_protocol()
    {
        // The half that actually matters for the security posture: absence from
        // tools/list is cosmetic if a client can still name the tool directly.
        // Rejection is SDK behaviour, asserted in a comment in ToolSurface.cs
        // and nowhere else until now.
        using var trimmed = WithConfig(("Mcp:DisabledTools:0", "view_attachment"));
        await using var client = await ConnectAsync(trimmed);

        var ex = await Should.ThrowAsync<McpException>(() =>
            client.CallToolAsync("view_attachment", new Dictionary<string, object?>
            {
                ["id"] = 1,
                ["partIndex"] = 0,
            }).AsTask());

        ex.Message.ShouldContain("view_attachment");
    }

    [Fact]
    public async Task String_encoded_number_is_coerced_at_the_json_boundary()
    {
        // Models routinely send "3" where the schema says integer. The direct
        // tool tests pass a typed int and can never exercise this; coercion is
        // SDK behaviour and therefore something an SDK bump can change.
        await using var client = await ConnectAsync();
        Seed("coerce-a@x", body: "udon soup");
        Seed("coerce-b@x", body: "udon broth");

        var body = await CallJsonAsync(client, "search_emails", new()
        {
            ["query"] = "udon",
            ["mode"] = "keyword",
            ["limit"] = "1", // string, not int
        });

        body.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Domain_errors_come_back_as_isError_with_a_readable_message()
    {
        // A tool-level failure must arrive as a tool result Claude can read and
        // act on, not as a transport error that reads like the server is down.
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("get_attachment_text", new Dictionary<string, object?>
        {
            ["id"] = 999999,
            ["partIndex"] = 0,
        });

        result.IsError.ShouldBe(true);
        var text = result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
        text.ShouldContain("999999", Case.Insensitive);
    }

    [Fact]
    public async Task Argument_binding_failure_is_reported_without_naming_the_parameter()
    {
        // KNOWN LIMITATION, pinned deliberately. A string where the schema says
        // boolean fails to bind, and the client is told only "An error occurred
        // invoking 'search_emails'." — no parameter name, no expected type, so
        // the model has nothing to self-correct from and typically retries the
        // same call. Contrast the domain-error test above, which names the id.
        // If a future SDK version enriches this, update the assertion (and
        // consider dropping any workaround built on top of it) — do not delete
        // the test, the shape of a binding failure is a client-facing contract.
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("search_emails", new Dictionary<string, object?>
        {
            ["mode"] = "keyword",
            ["hasAttachments"] = "true", // string, not bool
        });

        result.IsError.ShouldBe(true);
        var text = result.Content.OfType<TextContentBlock>().ShouldHaveSingleItem().Text;
        text.ShouldContain("search_emails");
        text.ShouldNotContain("hasAttachments", Case.Insensitive);
    }

    [Fact]
    public async Task Server_identity_and_instructions_reach_clients()
    {
        // serverInfo.name is the protocol identifier clients key their configs
        // off, and the version is the cheapest possible "which build am I
        // talking to" diagnostic. ServerInstructions is the one place the
        // live-mailbox framing is established — without it models describe
        // Mailvec to users as a static "archive" (see Program.cs).
        await using var client = await ConnectAsync();

        client.ServerInfo.Name.ShouldBe("mailvec");
        client.ServerInfo.Title.ShouldBe("Mailvec");

        // Shape only, deliberately. ConfigureServerInfo reads
        // Assembly.GetEntryAssembly(), which in-process resolves to the xunit
        // test host (it reports the runner's version here, not Mailvec's), so
        // the value can only be pinned against a real process — /health's
        // version field is the assertion that does that, and the live-server
        // check in ops/UPGRADING.md is the manual one.
        client.ServerInfo.Version.ShouldNotBeNull();
        client.ServerInfo.Version.ShouldMatch(@"^\d+\.\d+\.\d+$");

        client.ServerInstructions.ShouldNotBeNullOrWhiteSpace();
        client.ServerInstructions.ShouldContain("read-only");
    }

    // ---------- plumbing ----------

    /// <summary>
    /// Connects the SDK client to the in-process server. Passing no factory
    /// uses the class fixture; pass one from <see cref="WithConfig"/> to test a
    /// different deployment configuration.
    /// </summary>
    private async Task<McpClient> ConnectAsync(WebApplicationFactory<Program>? factory = null)
    {
        var http = (factory ?? _factory).CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/") },
            http);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>
    /// A sibling server with extra configuration, applied through BOTH
    /// <c>UseSetting</c> and <c>ConfigureAppConfiguration</c> — because the two
    /// halves of Program.cs read config at different times and neither
    /// mechanism alone covers both:
    ///
    /// <list type="bullet">
    /// <item><c>Mcp:DisabledTools</c> is read from <c>builder.Configuration</c>
    /// at *builder* time (tool registration precedes the options pipeline — see
    /// Program.cs), and ConfigureAppConfiguration sources are only merged at
    /// <c>Build()</c>. Set it that way and the tool stays registered while the
    /// test reads as though it exercised the option.</item>
    /// <item>Everything bound through <c>IOptions</c> is read post-Build, where
    /// the base fixture's own ConfigureAppConfiguration entries would otherwise
    /// override a UseSetting value from here.</item>
    /// </list>
    ///
    /// Setting both is what makes this helper correct regardless of which read
    /// path the option under test uses. Note neither is an environment
    /// variable, so nothing can leak into a concurrently starting host.
    /// </summary>
    private WebApplicationFactory<Program> WithConfig(params (string Key, string? Value)[] settings) =>
        _factory.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)));
        });

    /// <summary>
    /// Inserts one message into the fixture database. Every nullable field the
    /// response contract names is populated, because null members are omitted
    /// from the JSON — a sparse fixture would make a field-name assertion fail
    /// for a reason that has nothing to do with the contract.
    /// </summary>
    private long Seed(string messageId, string body)
    {
        var repo = new MessageRepository(new ConnectionFactory(
            Options.Create(new ArchiveOptions { DatabasePath = _factory.DatabasePath })));
        return repo.Upsert(
            Helpers.Sample(messageId, body: body, fromName: "Alice Example"),
            "INBOX", $"INBOX/cur/{messageId}", messageId, DateTimeOffset.UtcNow).Id;
    }

    /// <summary>Calls a tool and parses its single text block as JSON.</summary>
    private static async Task<JsonElement> CallJsonAsync(
        McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        result.IsError.ShouldNotBe(true, $"{tool} returned an error result");
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static JsonElement Properties(JsonElement schema) =>
        schema.TryGetProperty("properties", out var props) ? props : default;

    private static IReadOnlyList<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var req)
            ? req.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];

    private static void ShouldHaveKeys(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            obj.TryGetProperty(key, out _).ShouldBeTrue($"response must carry '{key}'");
        }
    }
}
