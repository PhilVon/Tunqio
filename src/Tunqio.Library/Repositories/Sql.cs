using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;

namespace Tunqio.Library.Repositories;

/// <summary>Small SQL helpers shared by the repositories: parameters, LIKE escaping, keyset clauses.</summary>
internal static class Sql
{
    /// <summary>U+FFFF as SQL text: the sentinel every real string sorts before (<see cref="SortKeys.NoText"/>).</summary>
    public const string NoText = "char(65535)";

    public static string NoTime { get; } = SortKeys.NoTime.ToString(CultureInfo.InvariantCulture);

    public static string NoYear { get; } = SortKeys.NoYear.ToString(CultureInfo.InvariantCulture);

    public static string NoRating { get; } = SortKeys.NoRating.ToString(CultureInfo.InvariantCulture);

    public static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static SqliteParameter Add(this SqliteCommand command, string name, object? value)
    {
        SqliteParameter parameter = command.Parameters.Add(name, TypeFor(value));
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    /// <summary>Replaces the value of an existing parameter (prepared statements in the upsert loop).</summary>
    public static void Set(this SqliteCommand command, string name, object? value) => command.Parameters[name].Value = value ?? DBNull.Value;

    /// <summary>A <c>%text%</c> pattern with <c>%</c>, <c>_</c> and <c>\</c> escaped; pair with <c>ESCAPE '\'</c>.</summary>
    public static string Like(string text) => Pattern(text, leading: "%");

    /// <summary>A <c>text%</c> pattern (escaped as <see cref="Like"/>): "starts with", for ranking.</summary>
    public static string StartsWith(string text) => Pattern(text, leading: string.Empty);

    private static string Pattern(string text, string leading)
    {
        var pattern = new StringBuilder(leading, text.Length + 2);
        foreach (char c in text)
        {
            if (c is '%' or '_' or '\\')
            {
                pattern.Append('\\');
            }

            pattern.Append(c);
        }

        return pattern.Append('%').ToString();
    }

    /// <summary>
    /// Appends the keyset predicate <c>(k1, k2, ..., id) &gt; ($c0, $c1, ..., $cid)</c> (or <c>&lt;</c> when
    /// descending) and binds the cursor's keys. <paramref name="keys"/> are the SQL key expressions in sort order.
    /// </summary>
    public static void AppendKeyset(StringBuilder where, SqliteCommand command, IReadOnlyList<string> keys, string idExpression, PageCursor cursor, bool descending)
    {
        if (cursor.Keys.Count != keys.Count)
        {
            throw new ArgumentException($"The cursor has {cursor.Keys.Count} key(s) but this sort has {keys.Count}; use the cursor of the same query.", nameof(cursor));
        }

        where.Append(" AND (").Append(string.Join(", ", keys)).Append(", ").Append(idExpression).Append(") ")
            .Append(descending ? '<' : '>').Append(" (");
        for (int i = 0; i < keys.Count; i++)
        {
            string name = "$c" + i.ToString(CultureInfo.InvariantCulture);
            where.Append(name).Append(", ");
            command.Add(name, cursor.Keys[i]);
        }

        where.Append("$cid)");
        command.Add("$cid", cursor.Id);
    }

    /// <summary><c>ORDER BY k1 [DESC], ..., id [DESC]</c>.</summary>
    public static string OrderBy(IReadOnlyList<string> keys, string idExpression, bool descending)
    {
        string direction = descending ? " DESC" : string.Empty;
        return " ORDER BY " + string.Join(", ", keys.Select(k => k + direction)) + ", " + idExpression + direction;
    }

    public static int? Int(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static long? Long(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public static double? Double(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    public static string? Text(this SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static SqliteType TypeFor(object? value) => value switch
    {
        null => SqliteType.Text,
        string => SqliteType.Text,
        int or long or bool or short or byte => SqliteType.Integer,
        double or float => SqliteType.Real,
        byte[] => SqliteType.Blob,
        _ => throw new ArgumentException($"Unsupported parameter type {value.GetType().Name}", nameof(value)),
    };
}
