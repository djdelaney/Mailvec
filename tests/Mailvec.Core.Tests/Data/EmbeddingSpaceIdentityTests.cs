using Mailvec.Core.Data;
using Mailvec.Core.Embedding;
using Mailvec.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Data;

/// <summary>
/// v11 embedding-space identity: the metadata stamps, the migration's
/// derive-from-stored rule, the code-side config-hash stamping, and the
/// switch-model atomicity. Phase 1 of docs/proposals/embedding-providers.md.
/// </summary>
public class EmbeddingSpaceIdentityTests
{
    [Fact]
    public void Fresh_database_stamps_space_id_and_config_hash()
    {
        using var db = new TempDatabase();
        var defaults = new OllamaOptions();
        var (expectedSpaceId, expectedHash) = EmbeddingSpace.FromOllamaOptions(defaults);

        Metadata(db, EmbeddingSpace.SpaceIdKey).ShouldBe(expectedSpaceId);
        Metadata(db, EmbeddingSpace.SpaceIdKey).ShouldBe("ollama:mxbai-embed-large:1024");
        Metadata(db, EmbeddingSpace.ConfigHashKey).ShouldBe(expectedHash);
    }

    [Fact]
    public void Fresh_database_substitutes_configured_model_into_the_space_id()
    {
        using var db = new TempDatabase(migrate: false);
        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions
            {
                EmbeddingModel = "qwen3-embedding:4b",
                EmbeddingDimensions = 2560,
            }));
        migrator.EnsureUpToDate();

        Metadata(db, EmbeddingSpace.SpaceIdKey).ShouldBe("ollama:qwen3-embedding:4b:2560");
        Metadata(db, EmbeddingSpace.ConfigHashKey).ShouldNotBeNull();
    }

    [Fact]
    public void Migration_derives_the_space_id_from_stored_metadata_not_config()
    {
        // A v10 database whose stored identity disagrees with the binary's
        // config: the migration must record what the vectors ARE (stored
        // values), never what config wishes they were — and the config hash
        // must stay unstamped, because a hash asserted from mismatched config
        // would be a false provenance claim.
        using var db = new TempDatabase();
        SimulateV10(db, storedModel: "some-legacy-model", storedDims: "768");

        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions())); // mxbai/1024 config
        migrator.EnsureUpToDate();

        Metadata(db, EmbeddingSpace.SpaceIdKey).ShouldBe("ollama:some-legacy-model:768");
        Metadata(db, EmbeddingSpace.ConfigHashKey).ShouldBeNull();
    }

    [Fact]
    public void Config_hash_self_heals_when_absent_and_config_agrees_with_stored_identity()
    {
        using var db = new TempDatabase();
        SimulateV10(db, storedModel: "mxbai-embed-large", storedDims: "1024");

        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));
        migrator.EnsureUpToDate();

        Metadata(db, EmbeddingSpace.SpaceIdKey).ShouldBe("ollama:mxbai-embed-large:1024");
        Metadata(db, EmbeddingSpace.ConfigHashKey)
            .ShouldBe(EmbeddingSpace.FromOllamaOptions(new OllamaOptions()).ConfigHash);
    }

    [Fact]
    public void An_existing_config_hash_is_never_overwritten_by_startup_stamping()
    {
        // The stored hash is an observation about how the vectors were
        // produced. A binary started with a different query prefix must not
        // quietly replace it — the mismatch is the signal the embedder's
        // verify (and health) act on.
        using var db = new TempDatabase();
        var original = Metadata(db, EmbeddingSpace.ConfigHashKey);
        original.ShouldNotBeNull();

        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions
            {
                QueryInstructionPrefix = "Instruct: something new\nQuery: ",
            }));
        migrator.EnsureUpToDate();

        Metadata(db, EmbeddingSpace.ConfigHashKey).ShouldBe(original);
    }

    [Fact]
    public void SwitchEmbeddingModel_stamps_space_and_hash_with_the_model_metadata()
    {
        using var db = new TempDatabase();
        var options = new OllamaOptions();
        var migrator = new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance,
            Microsoft.Extensions.Options.Options.Create(options));

        migrator.SwitchEmbeddingModel("qwen3-embedding:4b", 2560);

        Metadata(db, "embedding_model").ShouldBe("qwen3-embedding:4b");
        Metadata(db, "embedding_dimensions").ShouldBe("2560");
        Metadata(db, EmbeddingSpace.SpaceIdKey).ShouldBe("ollama:qwen3-embedding:4b:2560");
        Metadata(db, EmbeddingSpace.ConfigHashKey).ShouldBe(EmbeddingSpace.ComputeConfigHash(
            "ollama:qwen3-embedding:4b:2560", "qwen3-embedding:4b", 2560,
            options.QueryInstructionPrefix, documentPrefix: ""));
    }

    [Fact]
    public void Changing_the_query_prefix_changes_the_config_hash_but_not_the_space_id()
    {
        var withoutPrefix = EmbeddingSpace.FromOllamaOptions(new OllamaOptions());
        var withPrefix = EmbeddingSpace.FromOllamaOptions(new OllamaOptions
        {
            QueryInstructionPrefix = "Instruct: retrieve passages\nQuery: ",
        });

        withPrefix.SpaceId.ShouldBe(withoutPrefix.SpaceId);
        withPrefix.ConfigHash.ShouldNotBe(withoutPrefix.ConfigHash);
    }

    [Fact]
    public void The_config_hash_is_deterministic_and_field_boundary_safe()
    {
        var a = EmbeddingSpace.ComputeConfigHash("s", "m", 1024, "p", "");
        var b = EmbeddingSpace.ComputeConfigHash("s", "m", 1024, "p", "");
        a.ShouldBe(b);

        // Length-prefixing means moving characters across a field boundary
        // can never serialize identically.
        EmbeddingSpace.ComputeConfigHash("s", "m", 1024, "px", "")
            .ShouldNotBe(EmbeddingSpace.ComputeConfigHash("s", "m", 1024, "p", "x"));
    }

    /// <summary>
    /// Rewind a fresh (latest-version) database to the v10 shape: no space
    /// id, no config hash, schema_version stamped 10, and the stored
    /// model/dimensions set as given. The v11 migration is pure metadata, so
    /// this reproduces its input state exactly.
    /// </summary>
    private static void SimulateV10(TempDatabase db, string storedModel, string storedDims)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM metadata WHERE key IN ('embedding_space_id', 'embedding_config_hash');
            UPDATE metadata SET value = '10' WHERE key = 'schema_version';
            UPDATE metadata SET value = $m WHERE key = 'embedding_model';
            UPDATE metadata SET value = $d WHERE key = 'embedding_dimensions';
            """;
        cmd.Parameters.AddWithValue("$m", storedModel);
        cmd.Parameters.AddWithValue("$d", storedDims);
        cmd.ExecuteNonQuery();
    }

    private static string? Metadata(TempDatabase db, string key)
    {
        using var conn = db.Connections.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM metadata WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }
}
