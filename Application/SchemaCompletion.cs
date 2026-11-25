using System.Text.RegularExpressions;

namespace RecuperadorFirebird.Application;

public sealed record SchemaChange(string Kind, string Name, string Statement, string? BlockedReason = null);

public static class SchemaCompletion
{
    private const string Id = MetadataScriptComposer.Identifier;
    private const string Columns = """\((?<columns>[^)]*)\)""";
    private static readonly Regex Key = new(
        $@"^(?:CONSTRAINT\s+(?<name>{Id})\s+)?(?<kind>PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE)\s*{Columns}", RegexOptions.IgnoreCase);
    private static readonly Regex Reference = new(
        $@"\bREFERENCES\s+(?<table>{Id})(?:\s*\((?<columns>[^)]*)\))?", RegexOptions.IgnoreCase);
    private static readonly Regex Object = new(
        $@"^(?:CREATE(?:\s+OR\s+ALTER)?|RECREATE|ALTER)\s+(?<type>EXCEPTION|VIEW|PROCEDURE|TRIGGER)\s+(?<name>{Id})",
        RegexOptions.IgnoreCase);
    private static readonly Regex Index = new(
        $@"^CREATE\s+(?:UNIQUE\s+)?(?:(?:ASC(?:ENDING)?|DESC(?:ENDING)?)\s+)?INDEX\s+(?<name>{Id})\s+ON\s+(?<table>{Id})",
        RegexOptions.IgnoreCase);
    private static readonly Regex InlineKey = new(
        $"""(?:--[^\r\n]*|/\*[\s\S]*?\*/|'(?:''|[^'])*'|"(?:""|[^"])*")|\b(?:CONSTRAINT\s+(?<name>{Id})\s+)?(?<type>PRIMARY\s+KEY|UNIQUE|REFERENCES)\b""",
        RegexOptions.IgnoreCase);
    private static readonly Regex ExceptionReference = new(
        $@"\bEXCEPTION\s+(?<name>{Id})(?=\s*(?:;|USING\b|DO\b))", RegexOptions.IgnoreCase);

    public static IReadOnlyList<SchemaChange> Compare(string metadata, string catalog)
    {
        var existing = new Catalog(catalog);
        var tables = new List<SchemaChange>();
        var fields = new List<SchemaChange>();
        var keys = new List<SchemaChange>();
        var indexes = new List<SchemaChange>();
        var objects = new List<SchemaChange>();
        var plannedKeys = new HashSet<string>(StringComparer.Ordinal);
        var plannedObjects = new HashSet<string>(StringComparer.Ordinal);
        var declaredFields = new HashSet<(string, string)>();
        var foundDefinitions = false;
        foreach (var statement in MetadataScriptComposer.ReadStatements(metadata))
        {
            var table = Sql.Table.Match(statement);
            if (table.Success)
            {
                foundDefinitions = true;
                var name = Sql.Unquote(table.Groups["name"].Value);
                var start = statement.IndexOf('(', table.Length);
                if (start < 0)
                    throw new InvalidOperationException($"Definição de tabela sem colunas reconhecíveis: {name}.");
                var body = statement[start..];
                var items = MetadataScriptComposer.TableItems(body).ToArray();
                var kept = new List<string>();
                foreach (var item in items.Select(MetadataScriptComposer.TrimLeading))
                {
                    if (AddKey(name, item))
                        continue;
                    if (Regex.IsMatch(item, @"^(CONSTRAINT|CHECK)\b", RegexOptions.IgnoreCase))
                        continue;
                    kept.Add(ReadField(name, item, existing.Tables.Contains(name)));
                }
                if (!existing.Tables.Contains(name))
                {
                    if (existing.Objects.Contains(("VIEW", name)))
                        throw new InvalidOperationException($"O destino contém uma visão com o nome da tabela {name}.");
                    if (kept.Count == 0)
                        throw new InvalidOperationException($"Nenhum campo encontrado na definição da tabela {name}.");
                    var suffix = MetadataScriptComposer.AfterTableItems(body);
                    tables.Add(new SchemaChange("Tabela", name, statement[..start] + "(\n" + string.Join(",\n", kept) + "\n)" + suffix));
                }
                continue;
            }

            var alter = Regex.Match(statement, $@"^ALTER\s+TABLE\s+(?<name>{Id})\s+", RegexOptions.IgnoreCase);
            if (alter.Success)
            {
                var name = Sql.Unquote(alter.Groups["name"].Value);
                foreach (var item in MetadataScriptComposer.TableItems("(" + statement[alter.Length..] + ")"))
                {
                    var add = Regex.Match(MetadataScriptComposer.TrimLeading(item), @"^ADD\s+(?<definition>[\s\S]+)$", RegexOptions.IgnoreCase);
                    if (!add.Success)
                        continue;
                    var addition = MetadataScriptComposer.TrimLeading(add.Groups["definition"].Value);
                    if (AddKey(name, addition) || Regex.IsMatch(addition, @"^(CONSTRAINT|CHECK)\b", RegexOptions.IgnoreCase))
                        continue;
                    ReadField(name, Regex.Replace(addition, "^COLUMN\\s+", "", RegexOptions.IgnoreCase), schedule: true);
                }
                continue;
            }

            var index = Index.Match(statement);
            if (index.Success)
            {
                foundDefinitions = true;
                var indexName = Sql.Unquote(index.Groups["name"].Value);
                var indexTable = Sql.Unquote(index.Groups["table"].Value);
                if (existing.Indexes.TryGetValue(indexName, out var owner))
                {
                    if (owner != indexTable)
                        throw new InvalidOperationException($"O índice {indexName} já existe em {owner}, mas o SQL o define em {indexTable}.");
                }
                else if (plannedObjects.Add("INDEX|" + indexName))
                    indexes.Add(new SchemaChange("Índice", $"{indexTable}.{indexName}", statement));
                continue;
            }

            var definition = Object.Match(statement);
            if (!definition.Success)
                continue;
            foundDefinitions = true;
            var type = definition.Groups["type"].Value.ToUpperInvariant();
            var objectName = Sql.Unquote(definition.Groups["name"].Value);
            if (existing.Objects.Contains((type, objectName)))
                continue;
            // Preserve the sequence of procedure stubs and their later full bodies in IBExpert exports.
            var keyword = plannedObjects.Add(type + "|" + objectName) ? "CREATE " : "ALTER ";
            objects.Add(new SchemaChange(type, objectName, keyword + statement[definition.Groups["type"].Index..]));
        }

        if (!foundDefinitions)
            throw new InvalidOperationException("O arquivo SQL não contém definições de esquema reconhecíveis.");
        var availableExceptions = existing.Objects.Where(item => item.Item1 == "EXCEPTION").Select(item => item.Item2)
            .Concat(objects.Where(item => item.Kind == "EXCEPTION").Select(item => item.Name)).ToHashSet(StringComparer.Ordinal);
        var missingExceptions = objects.Where(item => item.Kind is "TRIGGER" or "PROCEDURE")
            .GroupBy(item => (item.Kind, item.Name))
            .ToDictionary(group => group.Key, group => group
                .SelectMany(item => ExceptionReference.Matches(MetadataScriptComposer.MaskQuotedComments(item.Statement)).Cast<Match>())
                .Select(match => Sql.Unquote(match.Groups["name"].Value)).Where(name => !availableExceptions.Contains(name))
                .Distinct(StringComparer.Ordinal).ToArray());
        var checkedObjects = objects.Select(item =>
            missingExceptions.TryGetValue((item.Kind, item.Name), out var names) && names.Length > 0
                ? item with { BlockedReason = "Exceção ausente: " + string.Join(", ", names) }
                : item).ToArray();
        return tables.Concat(fields).Concat(keys.Where(key => key.Kind == "PK"))
            .Concat(keys.Where(key => key.Kind == "UNQ")).Concat(keys.Where(key => key.Kind == "FK"))
            .Concat(indexes)
            .Concat(checkedObjects.Where(item => item.Kind == "EXCEPTION"))
            .Concat(checkedObjects.Where(item => item.Kind != "EXCEPTION")).ToArray();

        bool AddKey(string table, string definition)
        {
            var match = Key.Match(definition);
            if (!match.Success)
                return false;
            foundDefinitions = true;
            var kind = Regex.Replace(match.Groups["kind"].Value.ToUpperInvariant(), @"\s+", " ") switch
            {
                "PRIMARY KEY" => "PK",
                "FOREIGN KEY" => "FK",
                _ => "UNQ"
            };
            var name = match.Groups["name"].Success ? Sql.Unquote(match.Groups["name"].Value) : "";
            var columns = ReadNames(match.Groups["columns"].Value);
            var reference = kind == "FK" ? Reference.Match(definition, match.Length) : Match.Empty;
            var parent = reference.Success ? Sql.Unquote(reference.Groups["table"].Value) : "";
            var parentColumns = reference.Success ? ReadNames(reference.Groups["columns"].Value) : "";
            if (existing.Keys.Any(key => key.Table == table && key.Kind == kind &&
                (key.Name == name || (key.Columns == columns && key.Parent == parent &&
                (parentColumns.Length == 0 || key.ParentColumns == parentColumns)))))
                return true;
            if (kind == "PK" && existing.Keys.Any(key => key.Table == table && key.Kind == "PK"))
                throw new InvalidOperationException($"A tabela {table} já tem uma PK diferente. Revise a chave antes de continuar.");
            var label = name.Length == 0 ? $"{table} ({columns})" : $"{table}.{name}";
            if (plannedKeys.Add($"{table}|{kind}|{columns}|{parent}|{parentColumns}"))
                keys.Add(new SchemaChange(kind, label, $"ALTER TABLE {Sql.Name(table)} ADD {definition}"));
            return true;
        }

        string StripInlineKeys(string table, string field, string item, int offset)
        {
            var key = InlineKey.Matches(item, offset).FirstOrDefault(token => token.Groups["type"].Success);
            if (key is null)
                return item;
            var type = key.Groups["type"];
            var prefix = key.Groups["name"].Success ? "CONSTRAINT " + key.Groups["name"].Value + " " : "";
            var tail = item[(type.Index + type.Length)..];
            var notNull = Regex.Match(tail, @"\s+NOT\s+NULL\s*$", RegexOptions.IgnoreCase);
            if (notNull.Success)
                tail = tail[..notNull.Index];
            var definition = type.Value.Equals("REFERENCES", StringComparison.OrdinalIgnoreCase)
                ? $"FOREIGN KEY ({field}) REFERENCES{tail}"
                : $"{type.Value} ({field}){tail}";
            AddKey(table, prefix + definition);
            return item[..key.Index].TrimEnd() + (notNull.Success ? " NOT NULL" : "");
        }

        string ReadField(string table, string item, bool schedule)
        {
            var field = Regex.Match(item, $@"^(?<name>{Id})\s+");
            if (!field.Success)
                throw new InvalidOperationException($"Campo não reconhecido em {table}: {item}");
            foundDefinitions = true;
            var raw = field.Groups["name"].Value;
            var column = Sql.Unquote(raw);
            var columnDefinition = StripInlineKeys(table, raw, item, field.Length);
            if (declaredFields.Add((table, column)) && schedule && !existing.Fields.Contains((table, column)))
                fields.Add(new SchemaChange("Campo", $"{table}.{column}", $"ALTER TABLE {Sql.Name(table)} ADD {columnDefinition}"));
            return columnDefinition;
        }
    }

    public static string BuildScript(IReadOnlyList<SchemaChange> changes)
    {
        var applicable = changes.Where(change => change.BlockedReason is null).ToArray();
        var end = "^";
        while (applicable.Any(change => change.Statement.Contains(end, StringComparison.Ordinal)))
            end += "^";
        return $"SET BAIL ON;\nSET AUTODDL OFF;\nSET TERM {end} ;\n" +
            string.Concat(applicable.Select(change => change.Statement + "\n" + end + "\n")) +
            $"SET TERM ; {end}\nCOMMIT;\nQUIT;\n";
    }

    private static string ReadNames(string value) => string.Join(",", Regex.Matches(value, Id).Select(item => Sql.Unquote(item.Value)));

    private sealed record CatalogKey(string Table, string Kind, string Name, string Columns, string Parent, string ParentColumns);

    private sealed class Catalog
    {
        public HashSet<string> Tables { get; } = new(StringComparer.Ordinal);
        public HashSet<(string, string)> Fields { get; } = [];
        public HashSet<(string, string)> Objects { get; } = [];
        public Dictionary<string, string> Indexes { get; } = new(StringComparer.Ordinal);
        public CatalogKey[] Keys { get; }

        public Catalog(string text)
        {
            var keyRows = new List<string[]>();
            foreach (var line in text.Split('\n').Select(line => line.Trim()))
            {
                var parts = line.Split('|');
                switch (parts)
                {
                    case ["T", var table]: Tables.Add(table); break;
                    case ["C", var table, var column]: Fields.Add((table, column)); break;
                    case ["O", var kind, var name]: Objects.Add((kind, name)); break;
                    case ["I", var name, var table]: Indexes[name] = table; break;
                    case ["K", _, _, _, _, _, _]: keyRows.Add(parts); break;
                }
            }
            Keys = keyRows.GroupBy(row => (row[1], row[2], row[3])).Select(group => new CatalogKey(
                group.Key.Item1, group.Key.Item2, group.Key.Item3, string.Join(",", group.Select(row => row[4])),
                group.First()[5], string.Join(",", group.Select(row => row[6]).Where(name => name.Length != 0)))).ToArray();
        }
    }
}
