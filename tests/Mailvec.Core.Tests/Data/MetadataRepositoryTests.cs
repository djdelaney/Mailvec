using Mailvec.Core.Data;

namespace Mailvec.Core.Tests.Data;

public class MetadataRepositoryTests
{
    [Fact]
    public void Get_returns_null_for_unknown_key()
    {
        using var db = new TempDatabase();
        var repo = new MetadataRepository(db.Connections);

        repo.Get("nonexistent").ShouldBeNull();
    }

    [Fact]
    public void Set_then_Get_roundtrips_value()
    {
        using var db = new TempDatabase();
        var repo = new MetadataRepository(db.Connections);

        repo.Set("greeting", "hello");

        repo.Get("greeting").ShouldBe("hello");
    }

    [Fact]
    public void Set_upserts_when_key_exists()
    {
        using var db = new TempDatabase();
        var repo = new MetadataRepository(db.Connections);

        repo.Set("embedding_model", "mxbai-embed-large");
        repo.Set("embedding_model", "nomic-embed-text");

        repo.Get("embedding_model").ShouldBe("nomic-embed-text");
    }

    [Fact]
    public void Set_distinguishes_keys()
    {
        using var db = new TempDatabase();
        var repo = new MetadataRepository(db.Connections);

        repo.Set("a", "one");
        repo.Set("b", "two");

        repo.Get("a").ShouldBe("one");
        repo.Get("b").ShouldBe("two");
    }
}
