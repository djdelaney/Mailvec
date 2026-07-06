namespace Mailvec.Mcp.Tests;

/// <summary>
/// The empty-archive hint that rides on search_emails / list_folders. This is
/// the guard against the MCPB bundle's silent-failure mode: installed without
/// ever running ops/install.sh, the server creates a fresh empty DB and every
/// search returns zero results — the hint is what turns that into an
/// actionable message for the client LLM.
/// </summary>
public class SetupHintsTests
{
    [Fact]
    public void Non_empty_archive_gets_no_hint()
    {
        SetupHints.EmptyArchiveHint(1, sharedConfigExists: true, "/x/archive.sqlite").ShouldBeNull();
        SetupHints.EmptyArchiveHint(1, sharedConfigExists: false, "/x/archive.sqlite").ShouldBeNull();
    }

    [Fact]
    public void Empty_archive_without_shared_config_points_at_the_installer()
    {
        // The MCPB-installed-but-installer-never-ran case.
        var hint = SetupHints.EmptyArchiveHint(0, sharedConfigExists: false, "/x/archive.sqlite");

        hint.ShouldNotBeNull();
        hint!.ShouldContain("ops/install.sh");
        hint.ShouldContain("shared configuration");
    }

    [Fact]
    public void Empty_archive_with_shared_config_points_at_status_and_doctor()
    {
        // Installed, but the indexer hasn't produced anything (or the DB path
        // is wrong) — telling this user to re-run the installer would be noise.
        var hint = SetupHints.EmptyArchiveHint(0, sharedConfigExists: true, "/x/archive.sqlite");

        hint.ShouldNotBeNull();
        hint!.ShouldContain("mailvec status");
        hint.ShouldContain("mailvec doctor");
        hint.ShouldContain("/x/archive.sqlite");
        hint.ShouldNotContain("ops/install.sh");
    }
}
