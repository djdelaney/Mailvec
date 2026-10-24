using Mailvec.Core.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mailvec.Core.Tests.Data;

public class MaildirFlagsTests
{
    // Shared with the migration-agreement test below, so the SQL backfill is
    // checked against every shape the parser is.
    public static TheoryData<string, bool> Paths() => new()
    {
        { "/mail/INBOX/new/1729753205.12345_1.host,U=8812", false },          // just delivered, no info
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,", false },       // info, no flags
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,S", true },
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,FS", true },
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,RST", true },
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,F", false },
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,R", false },
        // An `S` before the info suffix is part of the unique name, not a flag.
        { "/mail/INBOX/cur/1729753205.S12345_1.HOST,U=8812:2,F", false },
        // A folder name containing `:2,S` must not read as flags.
        { "/mail/Odd:2,S/cur/1729753205.12345_1.host,U=8812:2,", false },
        { "/mail/Odd:2,S/cur/1729753205.12345_1.host,U=8812:2,S", true },
        // Lower-case letters are not the seen flag.
        { "/mail/INBOX/cur/1729753205.12345_1.host,U=8812:2,s", false },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public void IsSeen_reads_the_S_flag_from_the_info_suffix_only(string path, bool seen) =>
        MaildirFlags.IsSeen(path).ShouldBe(seen);

    [Fact]
    public void The_v14_seen_backfill_agrees_with_MaildirFlags()
    {
        // The migration's SQL expression and MaildirFlags.IsSeen answer the
        // same question in two languages; an existing database gets the first,
        // every later write the second. Drift between them would leave rows
        // whose read state silently flips on the next rename. Simulate a v13
        // database holding every test path, migrate it, and compare.
        using var db = new TempDatabase();
        using (var conn = db.Connections.Open())
        {
            foreach (var row in Paths())
            {
                using var insert = conn.CreateCommand();
                insert.CommandText = "INSERT INTO sync_state (maildir_full_path, message_id, last_seen_at) VALUES ($p, 'm@x', '2026-01-01T00:00:00Z')";
                insert.Parameters.AddWithValue("$p", (string)row[0]);
                insert.ExecuteNonQuery();
            }
            using var downgrade = conn.CreateCommand();
            downgrade.CommandText = """
                ALTER TABLE sync_state DROP COLUMN seen;
                UPDATE metadata SET value = '13' WHERE key = 'schema_version';
                """;
            downgrade.ExecuteNonQuery();
        }

        new SchemaMigrator(db.Connections, NullLogger<SchemaMigrator>.Instance).EnsureUpToDate();

        using var verify = db.Connections.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT maildir_full_path, seen FROM sync_state";
        using var reader = read.ExecuteReader();
        var checkedRows = 0;
        while (reader.Read())
        {
            var path = reader.GetString(0);
            (reader.GetInt64(1) == 1).ShouldBe(MaildirFlags.IsSeen(path), path);
            checkedRows++;
        }
        checkedRows.ShouldBe(Paths().Count);
    }

    [Fact]
    public void SyncState_upsert_derives_seen_from_the_path()
    {
        // No caller passes seen: it follows the path, so a rename to a new
        // flag set is all it takes to change it.
        using var db = new TempDatabase();
        var syncState = new SyncStateRepository(db.Connections);
        const string unseen = "/mail/INBOX/cur/1.host,U=1:2,";
        const string seen = "/mail/INBOX/cur/1.host,U=1:2,S";

        using (var conn = db.Connections.Open())
        using (var tx = conn.BeginTransaction())
        {
            syncState.Upsert(conn, tx, unseen, "m@x", DateTimeOffset.UtcNow, "h", "INBOX", null, null);
            syncState.Upsert(conn, tx, seen, "m@x", DateTimeOffset.UtcNow, "h", "INBOX", null, null);
            tx.Commit();
        }

        using var verify = db.Connections.Open();
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT maildir_full_path, seen FROM sync_state ORDER BY maildir_full_path";
        using var reader = read.ExecuteReader();
        reader.Read().ShouldBeTrue();
        (reader.GetString(0), reader.GetInt64(1)).ShouldBe((unseen, 0L));
        reader.Read().ShouldBeTrue();
        (reader.GetString(0), reader.GetInt64(1)).ShouldBe((seen, 1L));
    }
}
