using System.Text;
using System.Text.RegularExpressions;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Application;

internal static class Sql
{
    public static string Name(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    public static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

    public static readonly Regex Table = new(
        """^CREATE\s+(?:(?<temporary>GLOBAL\s+TEMPORARY)\s+)?TABLE\s+(?<name>"(?:""|[^"])*"|[^\s(]+)""",
        RegexOptions.IgnoreCase);
    public static readonly Regex Generator = new(
        """^CREATE\s+(?:GENERATOR|SEQUENCE)\s+(?<name>"(?:""|[^"])*"|[^\s;]+)""",
        RegexOptions.IgnoreCase);

    public static string Unquote(string value) => FirebirdName.Normalize(value);

    public static IEnumerable<string> Statements(string source)
    {
        var offset = 0;
        foreach (Match token in Regex.Matches(source, """/\*[\s\S]*?\*/|--[^\r\n]*|'(?:''|[^'])*'|"(?:""|[^"])*"|;"""))
        {
            if (token.Value != ";")
                continue;

            var statement = source[offset..token.Index];
            offset = token.Index + 1;
            statement = Regex.Replace(statement, """\A(?:\s+|/\*[\s\S]*?\*/|--[^\r\n]*)*""", "");
            if (statement.Length > 0)
                yield return statement;
        }
    }

    public static string? FromShowTable(string table, string output)
    {
        var fields = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var field = Regex.Match(line,
                """^([A-Z][A-Z0-9_$]*)\s{2,}(.*?)\s+(Not Null|Nullable)(?:\s+(DEFAULT .+?))?\s*$""",
                RegexOptions.IgnoreCase);
            if (!field.Success)
                continue;

            var type = field.Groups[2].Value;
            var domain = Regex.Match(type, @"^\(([^)]+)\) ");
            if (domain.Success)
                type = Name(domain.Groups[1].Value);
            else
                type = Regex.Replace(type, @"^BLOB segment (\d+), subtype (\w+)", "BLOB SUB_TYPE $2 SEGMENT SIZE $1");
            var defaultValue = field.Groups[4].Success ? " " + field.Groups[4].Value : "";
            var required = field.Groups[3].Value.Equals("Not Null", StringComparison.OrdinalIgnoreCase) ? " NOT NULL" : "";
            fields.Add($"{Name(field.Groups[1].Value)} {type}{defaultValue}{required}");
        }

        if (fields.Count == 0)
            return null;

        var primary = Regex.Match(output, @"(?im)^\s+Primary key (\([^\r\n]+\))");
        if (primary.Success)
            fields.Add("PRIMARY KEY " + primary.Groups[1].Value);

        return $"CREATE TABLE {Name(table)} ({string.Join(",\n", fields)})";
    }

    public static string CopyTable(string source, string table, IReadOnlyList<string> columns)
    {
        var quotedTable = Name(table);
        var names = string.Join(", ", columns.Select(Name));
        var variables = string.Join(", ", Enumerable.Range(0, columns.Count).Select(index => $":V{index}"));
        var sql = new StringBuilder();
        sql.AppendLine($"SELECT {Literal(table)} AS TRANSFERINDO FROM RDB$DATABASE^");
        sql.AppendLine("EXECUTE BLOCK RETURNS (LINHAS BIGINT) AS");
        for (var index = 0; index < columns.Count; index++)
            sql.AppendLine($"  DECLARE VARIABLE V{index} TYPE OF COLUMN {quotedTable}.{Name(columns[index])};");
        sql.AppendLine("BEGIN LINHAS = 0;");
        sql.AppendLine($"  FOR EXECUTE STATEMENT {Literal($"SELECT {names} FROM {quotedTable}")}");
        sql.AppendLine($"  ON EXTERNAL DATA SOURCE {Literal(source)} AS USER 'SYSDBA' INTO {variables} DO");
        sql.AppendLine($"  BEGIN INSERT INTO {quotedTable} ({names}) VALUES ({variables}); LINHAS = LINHAS + 1; END");
        sql.AppendLine("  SUSPEND;");
        sql.AppendLine("END^");
        sql.AppendLine("COMMIT^");
        return sql.ToString();
    }
}
