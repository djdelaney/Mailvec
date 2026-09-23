using Microsoft.Data.Sqlite;

namespace Mailvec.Core.Data;

/// <summary>
/// Separates SQLite failures that are a property of the DATABASE right now —
/// another writer holding the lock, a full disk, an I/O error, a read-only or
/// corrupt file — from ones that are a property of the ROW being written (a
/// constraint violation, an oversized value).
/// </summary>
/// <remarks>
/// The distinction matters wherever a failure is counted against a document.
/// Before it existed, the embedder's isolation pass counted a
/// <c>SQLITE_BUSY</c> from its write-back as a strike against the message it
/// was writing: a maintenance command holding the writer lock, or a full
/// disk, quarantined perfectly good messages three strikes at a time. The same
/// shape as the vision and parser taxonomies — a condition of the
/// infrastructure is not evidence about the document — applied to the one
/// dependency every write path shares.
/// </remarks>
public static class SqliteFailures
{
    // Primary result codes (https://sqlite.org/rescode.html).
    private const int Busy = 5, Locked = 6, NoMem = 7, ReadOnly = 8, IoErr = 10,
        Corrupt = 11, Full = 13, CantOpen = 14, Protocol = 15, NotADb = 26;

    /// <summary>True when the failure says nothing about the row being written.</summary>
    public static bool IsDatabaseWide(SqliteException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // SqliteErrorCode is the primary code; extended codes (e.g.
        // SQLITE_BUSY_SNAPSHOT) share its low byte.
        return (ex.SqliteErrorCode & 0xFF) is Busy or Locked or NoMem or ReadOnly or IoErr
            or Corrupt or Full or CantOpen or Protocol or NotADb;
    }

    /// <inheritdoc cref="IsDatabaseWide(SqliteException)"/>
    public static bool IsDatabaseWide(Exception ex) =>
        ex is SqliteException sqlite && IsDatabaseWide(sqlite);
}
