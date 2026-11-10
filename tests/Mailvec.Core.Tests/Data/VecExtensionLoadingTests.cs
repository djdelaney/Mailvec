using Mailvec.Core.Data;
using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Tests.Data;

/// <summary>
/// sqlite-vec is a native library loaded into every process that opens the
/// archive. Two things kept more than that one library loadable: the SQL
/// function load_extension() was left enabled on every connection, and the
/// search for the library walked every ancestor directory to the filesystem
/// root.
/// </summary>
public sealed class VecExtensionLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mailvec-vecpath-" + Guid.NewGuid().ToString("N"));
    private const string Relative = "./runtimes/test-rid/native/vec0.test";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Plant(params string[] under)
    {
        var path = Path.Combine([_dir, .. under, "runtimes", "test-rid", "native", "vec0.test"]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not really a library");
        return path;
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_dir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void The_sql_load_extension_function_is_off_on_every_connection()
    {
        // vec0 itself is loaded through the C API for one call. Nothing a
        // statement can say should be able to load a second library.
        using var db = new TempDatabase();
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT load_extension('/nonexistent/library')";

        var ex = Should.Throw<SqliteException>(() => cmd.ExecuteScalar());

        ex.Message.ShouldContain("not authorized");
    }

    [Fact]
    public void Vec0_still_works_with_the_switch_off()
    {
        using var db = new TempDatabase();
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT vec_version()";

        cmd.ExecuteScalar().ShouldNotBeNull();
    }

    [Fact]
    public void A_library_beside_the_binary_is_found()
    {
        var expected = Plant("install", "mcp");

        ConnectionFactory.ResolveRelative(Relative, Dir("install", "mcp")).ShouldBe(expected);
    }

    [Fact]
    public void An_installed_binary_does_not_search_its_ancestors()
    {
        // No checkout above it: ~/.local/share/mailvec/mcp, /app/mcp, a
        // bundle. A runtimes/ tree further up is someone else's.
        Plant();                 // <tmp>/runtimes/...
        Plant("install");        // <tmp>/install/runtimes/...

        ConnectionFactory.ResolveRelative(Relative, Dir("install", "mcp")).ShouldBeNull();
    }

    [Fact]
    public void Inside_a_checkout_the_search_reaches_the_repo_root()
    {
        File.WriteAllText(Path.Combine(Dir("repo"), "Mailvec.slnx"), "");
        var expected = Plant("repo");

        ConnectionFactory.ResolveRelative(Relative, Dir("repo", "tests", "X", "bin", "Debug", "net10.0")).ShouldBe(expected);
    }

    [Fact]
    public void Inside_a_checkout_the_search_stops_at_the_repo_root()
    {
        File.WriteAllText(Path.Combine(Dir("outer", "repo"), "Mailvec.slnx"), "");
        Plant("outer");          // above the checkout

        ConnectionFactory.ResolveRelative(Relative, Dir("outer", "repo", "src", "X", "bin")).ShouldBeNull();
    }
}
