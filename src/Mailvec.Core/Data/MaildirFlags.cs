namespace Mailvec.Core.Data;

/// <summary>
/// Reads Maildir flags out of a message file's name — <c>&lt;unique&gt;:2,&lt;flags&gt;</c>,
/// where each flag is one ASCII letter (<c>S</c> seen, <c>R</c> replied,
/// <c>F</c> flagged, <c>T</c> trashed, <c>D</c> draft, <c>P</c> passed).
///
/// <para>Only the basename is examined: a folder name that happens to contain
/// <c>:2,</c> must not read as flags. A file with no info suffix (anything
/// mbsync has just delivered to <c>new/</c>) has no flags. The separator is
/// isync's default <c>FieldDelimiter</c>, <c>:</c>.</para>
///
/// <para><b>Migration 014 backfills <c>sync_state.seen</c> with a SQL
/// expression that must agree with <see cref="IsSeen"/></b> — change one and
/// change the other (pinned by
/// <c>SchemaMigratorTests.The_v14_seen_backfill_agrees_with_MaildirFlags</c>).</para>
/// </summary>
public static class MaildirFlags
{
    private const string InfoPrefix = ":2,";

    public static bool IsSeen(string maildirFullPath)
    {
        var name = Path.GetFileName(maildirFullPath);
        var at = name.IndexOf(InfoPrefix, StringComparison.Ordinal);
        return at >= 0 && name.AsSpan(at + InfoPrefix.Length).Contains('S');
    }
}
