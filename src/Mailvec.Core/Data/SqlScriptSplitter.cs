using System.Text;

namespace Mailvec.Core.Data;

/// <summary>
/// Splits a SQL script into individual statements at top-level semicolons,
/// respecting CREATE TRIGGER ... BEGIN ... END; blocks (whose internal
/// semicolons must not be treated as statement terminators).
/// Microsoft.Data.Sqlite's ExecuteNonQuery silently stops after the first
/// such trigger, so we tokenise statements ourselves and execute one by one.
/// </summary>
internal static class SqlScriptSplitter
{
    public static IReadOnlyList<string> Split(string script)
    {
        var statements = new List<string>();
        var sb = new StringBuilder();
        int beginDepth = 0;
        int i = 0;

        while (i < script.Length)
        {
            char c = script[i];
            char next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                while (i < script.Length && script[i] != '\n') { sb.Append(script[i]); i++; }
                continue;
            }
            if (c == '/' && next == '*')
            {
                sb.Append("/*"); i += 2;
                while (i < script.Length && !(script[i] == '*' && i + 1 < script.Length && script[i + 1] == '/'))
                {
                    sb.Append(script[i]); i++;
                }
                if (i < script.Length) { sb.Append("*/"); i += 2; }
                continue;
            }
            if (c == '\'' || c == '"')
            {
                var quote = c;
                sb.Append(c); i++;
                while (i < script.Length)
                {
                    sb.Append(script[i]);
                    if (script[i] == quote)
                    {
                        // doubled quote = escape; otherwise end of literal
                        if (i + 1 < script.Length && script[i + 1] == quote) { sb.Append(script[i + 1]); i += 2; continue; }
                        i++; break;
                    }
                    i++;
                }
                continue;
            }

            sb.Append(c);

            if (IsKeywordEndingAt(sb, "BEGIN")) beginDepth++;
            else if (beginDepth > 0 && IsKeywordEndingAt(sb, "END")) beginDepth--;

            if (c == ';' && beginDepth == 0)
            {
                var stmt = sb.ToString().Trim();
                if (stmt.Length > 1) statements.Add(stmt);
                sb.Clear();
            }
            i++;
        }

        var tail = sb.ToString().Trim();
        if (tail.Length > 0) statements.Add(tail);
        return statements;
    }

    private static bool IsKeywordEndingAt(StringBuilder sb, string keyword)
    {
        if (sb.Length < keyword.Length) return false;
        // The character just appended is the last of the keyword candidate.
        for (int k = 0; k < keyword.Length; k++)
        {
            var c = char.ToUpperInvariant(sb[sb.Length - keyword.Length + k]);
            if (c != keyword[k]) return false;
        }
        // Must be a whole-word match: char before the keyword (if any) is non-alphanumeric/underscore.
        if (sb.Length > keyword.Length)
        {
            var before = sb[sb.Length - keyword.Length - 1];
            if (char.IsLetterOrDigit(before) || before == '_') return false;
        }
        return true;
    }
}
