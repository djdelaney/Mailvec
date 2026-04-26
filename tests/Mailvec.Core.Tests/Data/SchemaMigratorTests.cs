namespace Mailvec.Core.Tests.Data;

public class SchemaMigratorTests
{
    [Fact]
    public void Creates_all_expected_tables_on_fresh_database()
    {
        using var db = new TempDatabase();
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";

        var tables = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) tables.Add(reader.GetString(0));

        tables.ShouldContain("messages");
        tables.ShouldContain("chunks");
        tables.ShouldContain("sync_state");
        tables.ShouldContain("metadata");
        tables.ShouldContain("messages_fts");
        tables.ShouldContain("chunk_embeddings");
    }
}
