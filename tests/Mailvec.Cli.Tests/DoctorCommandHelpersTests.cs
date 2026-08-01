using Mailvec.Cli.Commands;

namespace Mailvec.Cli.Tests;

/// <summary>
/// Coverage for the pure-logic helpers inside DoctorCommand. The full Run is
/// a 200-line async fan-out (launchctl shell-outs, optional Ollama ping,
/// HTTP /health probe) — we leave the orchestration alone and test only
/// the formatters + summary tallier here.
/// </summary>
public class DoctorCommandHelpersTests
{
    [Theory]
    // Wildcard binds are "listen everywhere", not a connectable address. The
    // container image bakes Mcp__BindAddress=0.0.0.0, so without this rewrite
    // doctor probes http://0.0.0.0:3333/health — which Linux forgives and
    // Windows/macOS refuse, reporting a healthy server as unreachable.
    [InlineData("0.0.0.0", 3333, "http://127.0.0.1:3333/health")]
    [InlineData("*", 3333, "http://127.0.0.1:3333/health")]
    [InlineData("::", 3333, "http://[::1]:3333/health")]
    // Anything specific must be passed through: a server bound to one
    // interface is only reachable on that interface, and silently probing
    // loopback instead would report the wrong thing as healthy.
    [InlineData("127.0.0.1", 3333, "http://127.0.0.1:3333/health")]
    [InlineData("192.168.1.50", 9000, "http://192.168.1.50:9000/health")]
    public void HealthProbeUrl_rewrites_only_wildcard_binds(string bind, int port, string expected)
    {
        DoctorCommand.HealthProbeUrl(bind, port).ShouldBe(expected);
    }

    [Fact]
    public void Container_deployment_reports_compose_as_the_supervisor_not_a_warning()
    {
        // The container IS the supported deployment, so a "no launchd" warning
        // there is noise on every single run — and an operator who learns to
        // skim doctor's warnings is one who misses the real one.
        var check = DoctorCommand.NonLaunchdServicesCheck(inContainer: true, platform: "Unix");

        check.Status.ShouldBe("ok");
        check.Detail.ShouldContain("compose");
        check.Detail.ShouldNotContain("launchctl");
    }

    [Fact]
    public void Non_macos_host_still_warns_about_launchd()
    {
        // A bare Linux host install genuinely has no supervisor — that one
        // must keep warning.
        var check = DoctorCommand.NonLaunchdServicesCheck(inContainer: false, platform: "Unix");

        check.Status.ShouldBe("warn");
        check.Detail.ShouldContain("Unix");
    }

    [Fact]
    public void Missing_mbsync_is_expected_in_a_container_and_does_not_warn()
    {
        // The app image never ships mbsync; IMAP sync runs in the sidecar.
        var check = DoctorCommand.MbsyncToolCheck(mbsyncPath: null, inContainer: true, isMacOs: false);

        check.Status.ShouldBe("ok");
        check.Detail.ShouldContain("sidecar");
        // The old text told a Linux container operator to run Homebrew.
        check.Detail.ShouldNotContain("brew");
    }

    [Theory]
    [InlineData(true, "brew install isync")]
    [InlineData(false, "package manager")]
    public void Missing_mbsync_on_a_host_warns_with_platform_appropriate_advice(bool isMacOs, string expected)
    {
        var check = DoctorCommand.MbsyncToolCheck(mbsyncPath: null, inContainer: false, isMacOs: isMacOs);

        check.Status.ShouldBe("warn");
        check.Detail.ShouldContain(expected);
    }

    [Fact]
    public void Present_mbsync_reports_its_path_regardless_of_deployment()
    {
        DoctorCommand.MbsyncToolCheck("/usr/bin/mbsync", inContainer: true, isMacOs: false)
            .ShouldSatisfyAllConditions(
                c => c.Status.ShouldBe("ok"),
                c => c.Detail.ShouldBe("/usr/bin/mbsync"));
    }

    [Fact]
    public void InContainer_is_false_on_a_normal_dev_machine()
    {
        // Guards the default: if this ever returns true off a container, the
        // launchd and mbsync checks silently stop warning on a real host
        // install, which is where those warnings actually matter.
        DoctorCommand.InContainer().ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(2 * 1024 * 1024, "2.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void FormatSize_renders_powers_of_two(long bytes, string expected)
    {
        DoctorCommand.FormatSize(bytes).ShouldBe(expected);
    }

    [Theory]
    [InlineData(30, "30s")]
    [InlineData(120, "2m")]
    [InlineData(60 * 60 * 5, "5h")]
    [InlineData(60 * 60 * 24 * 7, "7d")]
    public void HumanizeAge_picks_appropriate_unit(int seconds, string expected)
    {
        DoctorCommand.HumanizeAge(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Fact]
    public void Summarize_tallies_status_counts()
    {
        var checks = new List<DoctorCommand.DoctorCheck>
        {
            new("a", "ok",   "all good",     "config"),
            new("b", "warn", "minor issue",  "config"),
            new("c", "fail", "broken",       "services"),
            new("d", "ok",   "all good",     "tools"),
            new("e", "warn", "minor issue",  "tools"),
            // An unknown status should be ignored (defensive: future
            // refactors can't accidentally inflate any of the three buckets).
            new("f", "unknown", "?",         "tools"),
        };

        var (ok, warn, fail) = DoctorCommand.Summarize(checks);

        ok.ShouldBe(2);
        warn.ShouldBe(2);
        fail.ShouldBe(1);
    }

    [Fact]
    public void Summarize_returns_zeros_for_empty_list()
    {
        var (ok, warn, fail) = DoctorCommand.Summarize(new List<DoctorCommand.DoctorCheck>());
        ok.ShouldBe(0);
        warn.ShouldBe(0);
        fail.ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // Version check. The reason this exists: a green doctor run looks
    // IDENTICAL on every release, so "did my deploy actually land?" needed a
    // second command — and ops/redeploy.sh republishes services individually,
    // so a launchd install can serve mixed binaries with all other checks
    // passing.
    // -----------------------------------------------------------------

    [Fact]
    public void Matching_versions_report_lockstep()
    {
        var check = DoctorCommand.BuildVersionCheck("0.1.36", "0.1.36", skipNet: false);

        check.Status.ShouldBe("ok");
        check.Section.ShouldBe("config");
        check.Detail.ShouldContain("0.1.36");
        check.Detail.ShouldContain("lockstep");
    }

    [Fact]
    public void Mixed_binaries_warn_and_name_both_versions()
    {
        // The whole point of the check: ops/redeploy.sh mcp without cli (or
        // vice versa) leaves a skew that every other check reports as fine.
        // It warns rather than fails — mixed binaries are "fix this soon",
        // not "Mailvec is down".
        var check = DoctorCommand.BuildVersionCheck("0.1.35", "0.1.36", skipNet: false);

        check.Status.ShouldBe("warn");
        check.Detail.ShouldContain("0.1.35");
        check.Detail.ShouldContain("0.1.36");
        check.Detail.ShouldContain("redeploy");
    }

    [Fact]
    public void Unreachable_server_still_prints_the_cli_version_without_a_second_warning()
    {
        // /health being down already has its own check. Warning here too
        // would double-report one cause, and doctor's warnings are only
        // useful while they stay scarce.
        var check = DoctorCommand.BuildVersionCheck("0.1.36", serverVersion: null, skipNet: false);

        check.Status.ShouldBe("ok");
        check.Detail.ShouldContain("0.1.36");
    }

    [Fact]
    public void No_net_reports_the_cli_version_and_says_why_it_did_not_compare()
    {
        var check = DoctorCommand.BuildVersionCheck("0.1.36", serverVersion: null, skipNet: true);

        check.Status.ShouldBe("ok");
        check.Detail.ShouldContain("0.1.36");
        check.Detail.ShouldContain("--no-net");
    }

    [Fact]
    public void Unknown_version_on_either_side_is_reported_not_treated_as_drift()
    {
        // An assembly with no version attribute reads as "unknown". Comparing
        // it as a string would manufacture a drift warning out of missing
        // metadata.
        DoctorCommand.BuildVersionCheck("unknown", "0.1.36", skipNet: false).Status.ShouldBe("ok");
        DoctorCommand.BuildVersionCheck("0.1.36", "unknown", skipNet: false).Status.ShouldBe("ok");
    }

    [Theory]
    // The wire casing is the server's serializer policy, not ours — matching
    // only one casing would silently read as "couldn't reach it".
    [InlineData("{\"version\":\"0.1.36\",\"status\":\"ok\"}", "0.1.36")]
    [InlineData("{\"Version\":\"0.1.36\"}", "0.1.36")]
    public void TryReadVersion_extracts_the_version_regardless_of_casing(string body, string expected)
    {
        DoctorCommand.TryReadVersion(body).ShouldBe(expected);
    }

    [Theory]
    // Anything unreadable must yield null, never a fabricated version: a
    // proxy's HTML error page in front of /health would otherwise become a
    // bogus drift warning.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("{\"status\":\"ok\"}")]
    [InlineData("{\"version\":null}")]
    [InlineData("{\"version\":\"\"}")]
    [InlineData("{\"version\":123}")]
    [InlineData("[1,2,3]")]
    public void TryReadVersion_returns_null_for_anything_it_cannot_read(string body)
    {
        DoctorCommand.TryReadVersion(body).ShouldBeNull();
    }
}
