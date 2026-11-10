using System.Text.Json;
using Mailvec.Cli.Commands;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Doctor's machine-facing output contract. Scripts branch on the exit code
/// (0 = every check passed or warned, 1 = any fail) and the --json document
/// travels in bug reports, so its property names and summary counts are a
/// contract — a rename would break consumers with no compile-time signal.
/// </summary>
public sealed class DoctorCommandOutputTests
{
    private static List<DoctorCommand.DoctorCheck> Checks(params DoctorCommand.DoctorCheck[] checks) => [.. checks];

    [Fact]
    public void Exit_code_is_zero_for_ok_and_warn_but_one_for_any_fail()
    {
        var healthy = Checks(
            DoctorCommand.DoctorCheck.Ok("Database", "found", "config"),
            DoctorCommand.DoctorCheck.Warn("Maildir root", "missing (fresh install)", "config"));
        var broken = Checks(
            DoctorCommand.DoctorCheck.Ok("Database", "found", "config"),
            DoctorCommand.DoctorCheck.Fail("sqlite-vec", "not found", "config"));

        DoctorCommand.Emit(healthy, json: true, new StringWriter()).ShouldBe(0);
        DoctorCommand.Emit(broken, json: true, new StringWriter()).ShouldBe(1);
    }

    [Fact]
    public void Json_report_has_the_documented_shape_and_summary_counts()
    {
        var checks = Checks(
            DoctorCommand.DoctorCheck.Ok("Database", "/tmp/archive.sqlite (1.0 MB)", "config"),
            DoctorCommand.DoctorCheck.Warn("Maildir root", "not found", "config"),
            DoctorCommand.DoctorCheck.Fail("Ollama", "unreachable", "pipeline"));
        var sw = new StringWriter();

        DoctorCommand.Emit(checks, json: true, sw);

        using var doc = JsonDocument.Parse(sw.ToString());
        var root = doc.RootElement;

        // Summary counts, under the exact property names bug-report tooling reads.
        var summary = root.GetProperty("summary");
        summary.GetProperty("ok").GetInt32().ShouldBe(1);
        summary.GetProperty("warn").GetInt32().ShouldBe(1);
        summary.GetProperty("fail").GetInt32().ShouldBe(1);

        // Every check row carries section/name/status/detail, values intact.
        var rows = root.GetProperty("checks").EnumerateArray().ToList();
        rows.Count.ShouldBe(3);
        rows[2].GetProperty("section").GetString().ShouldBe("pipeline");
        rows[2].GetProperty("name").GetString().ShouldBe("Ollama");
        rows[2].GetProperty("status").GetString().ShouldBe("fail");
        rows[2].GetProperty("detail").GetString().ShouldBe("unreachable");
        rows.Select(r => r.GetProperty("status").GetString()).ShouldBe(["ok", "warn", "fail"]);
    }

    [Fact]
    public void The_services_heading_names_the_supervisor_actually_in_use()
    {
        // A container has no launchd, on any host OS; the heading used to say
        // "Services (launchd)" above checks that reported "no launchd in a container".
        DoctorCommand.ServicesHeading(isMacOs: true, inContainer: false).ShouldBe("Services (launchd)");
        DoctorCommand.ServicesHeading(isMacOs: false, inContainer: true).ShouldBe("Services (compose)");
        DoctorCommand.ServicesHeading(isMacOs: true, inContainer: true).ShouldBe("Services (compose)");
        DoctorCommand.ServicesHeading(isMacOs: false, inContainer: false).ShouldBe("Services");
    }

    [Fact]
    public void No_reused_message_ids_is_ok()
    {
        DoctorCommand.DivergentCopiesCheck(0, []).Status.ShouldBe("ok");
    }

    [Fact]
    public void Reused_message_ids_warn_and_name_where_to_look()
    {
        var check = DoctorCommand.DivergentCopiesCheck(7, ["a@example.com", "b@example.com"]);

        check.Status.ShouldBe("warn");   // usually a list copy: never a failure
        check.Detail.ShouldContain("7 Message-ID(s)");
        check.Detail.ShouldContain("<a@example.com>");
        check.Detail.ShouldContain("5 more");
        check.Detail.ShouldContain("not indexed");
    }
}
