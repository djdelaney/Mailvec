using System.Reflection;
using Mailvec.Mcp;
using ModelContextProtocol.Server;

namespace Mailvec.Mcp.Tests;

/// <summary>
/// Mcp:DisabledTools resolution — the server-side half of trimming the
/// remote tool surface before the tunnel goes live (docs/security.md
/// requires dropping the native-parser tools from any internet-fronted
/// deployment). Misresolution here is a security bug, not a cosmetic one:
/// a silently-ignored entry leaves the tool it meant to disable exposed.
/// </summary>
public class ToolSurfaceTests
{
    [Fact]
    public void Default_registers_the_full_locked_surface()
    {
        ToolSurface.Resolve(null).Count.ShouldBe(7);
        ToolSurface.Resolve([]).Count.ShouldBe(7);
        ToolSurface.Resolve(["", "  "]).Count.ShouldBe(7); // blanks ignored, not errors
    }

    [Fact]
    public void Disabling_the_remote_surface_tools_removes_exactly_those()
    {
        var enabled = ToolSurface.Resolve(["view_attachment", "get_attachment_page_image"]);

        enabled.Count.ShouldBe(5);
        enabled.ShouldNotContain(ToolSurface.All["view_attachment"]);
        enabled.ShouldNotContain(ToolSurface.All["get_attachment_page_image"]);
        enabled.ShouldContain(ToolSurface.All["search_emails"]);
        enabled.ShouldContain(ToolSurface.All["get_attachment_text"]); // the pure-DB read stays
    }

    [Fact]
    public void Names_are_case_insensitive_and_trimmed()
    {
        var enabled = ToolSurface.Resolve([" View_Attachment "]);

        enabled.Count.ShouldBe(6);
        enabled.ShouldNotContain(ToolSurface.All["view_attachment"]);
    }

    [Fact]
    public void Unknown_name_fails_startup_loudly()
    {
        // A typo'd entry must not be silently ignored — that would leave the
        // tool it meant to disable exposed on a public endpoint.
        var ex = Should.Throw<InvalidOperationException>(() => ToolSurface.Resolve(["view_attachmnet"]));

        ex.Message.ShouldContain("view_attachmnet");
        ex.Message.ShouldContain("view_attachment"); // the valid-names hint
    }

    [Fact]
    public void Map_matches_the_locked_attribute_names()
    {
        // ToolSurface.All must stay in lockstep with the
        // [McpServerTool(Name = ...)] attributes — the locked API contract in
        // CLAUDE.md. If a tool is renamed or added without updating the map,
        // this fails (and a new tool class outside the map would be silently
        // unregistrable via DisabledTools, so we also pin the count against
        // the assembly).
        foreach (var (name, type) in ToolSurface.All)
        {
            var attrNames = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
                .Where(a => a is not null)
                .Select(a => a!.Name)
                .ToList();
            attrNames.ShouldContain(name, $"type {type.Name} should carry [McpServerTool(Name = \"{name}\")]");
        }

        var toolClassesInAssembly = typeof(ToolSurface).Assembly.GetTypes()
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
            .ToList();
        toolClassesInAssembly.Count.ShouldBe(ToolSurface.All.Count,
            "every [McpServerTool] class must appear in ToolSurface.All or it can't be disabled");
    }
}
