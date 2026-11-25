using System.Text;
using System.Text.RegularExpressions;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Application;

public sealed record MetadataResult(int PrimaryKeys, int ForeignKeys, int Triggers, int Procedures, int Views, int Exceptions, int Omitted);
public sealed record GeneratedMetadataScript(string Content, MetadataResult Counts);

public static class MetadataScriptComposer
{
    private const string Identifier = """(?:"(?:""|[^"])*"|[\p{L}_$][\p{L}\p{N}_$]*)""";
    private static readonly Regex ObjectDefinition = new(
        $@"^(?:CREATE(?:\s+OR\s+ALTER)?|RECREATE|ALTER)\s+(?<kind>EXCEPTION|VIEW|PROCEDURE|TRIGGER)\s+(?<name>{Identifier})",
        RegexOptions.IgnoreCase);
    private static readonly Regex Key = new(
        $@"^(?:CONSTRAINT\s+(?<name>{Identifier})\s+)?(?<kind>PRIMARY\s+KEY|FOREIGN\s+KEY)\s*\(",
        RegexOptions.IgnoreCase);
    private static readonly Regex TargetTable = new($@"\bREFERENCES\s+(?<table>{Identifier})", RegexOptions.IgnoreCase);
    private static readonly Regex TriggerTable = new($@"^\s*(?:FOR\s+|(?:(?:ACTIVE|INACTIVE)\s+)?(?:BEFORE|AFTER)\s+[^\r\n]*?\s+ON\s+)(?<table>{Identifier})", RegexOptions.IgnoreCase);
    private static readonly Regex Lexeme = new(
        """--[^\r\n]*|/\*(?:(?>[^*/]+|/(?!\*)|\*(?!/))|/\*(?<d>)|\*/(?<-d>))*(?(d)(?!))\*/|'(?:''|[^'])*'|"(?:""|[^"])*"|[(),]""");

    private sealed record Constraint(string Table, string? Reference, string? Name, string Definition, bool Primary);
    private sealed record Definition(string Kind, string Name, string? Table, string Sql);
    private sealed record MetadataPlan(List<Constraint> Primary, List<Constraint> Foreign, List<Definition> Objects, List<string> Omitted);

    public static GeneratedMetadataScript Compose(string text, SchemaSelection selection)
    {
        var plan = ReadPlan(text, selection);
        if (plan.Primary.Count + plan.Foreign.Count + plan.Objects.Count == 0)
            throw new InvalidOperationException("O arquivo não contém PK, FK, gatilhos, procedimentos, visões ou exceções reconhecíveis.");
        int Count(string kind) => plan.Objects.Where(item => item.Kind == kind).Select(item => item.Name).Distinct(StringComparer.Ordinal).Count();
        return new GeneratedMetadataScript(Render(plan), new MetadataResult(plan.Primary.Count, plan.Foreign.Count,
            Count("TRIGGER"), Count("PROCEDURE"), Count("VIEW"), Count("EXCEPTION"), plan.Omitted.Count));
    }

    private static MetadataPlan ReadPlan(string text, SchemaSelection selection)
    {
        var primary = new List<Constraint>();
        var foreign = new List<Constraint>();
        var objects = new List<Definition>();
        var omitted = new List<string>();
        foreach (var statement in ReadStatements(text))
        {
            var table = Sql.Table.Match(statement);
            if (table.Success)
            {
                var rest = statement[table.Length..].TrimStart();
                if (rest.StartsWith('('))
                    foreach (var item in TableItems(rest))
                        AddConstraint(Sql.Unquote(table.Groups["name"].Value), item, inline: true);
                continue;
            }
            var alter = Regex.Match(statement, $@"^ALTER\s+TABLE\s+(?<table>{Identifier})\s+", RegexOptions.IgnoreCase);
            if (alter.Success)
            {
                var relationName = Sql.Unquote(alter.Groups["table"].Value);
                foreach (var action in TableItems("(" + statement[alter.Length..] + ")"))
                {
                    var add = Regex.Match(TrimLeading(action), @"^ADD\s+(?<definition>[\s\S]+)$", RegexOptions.IgnoreCase);
                    if (add.Success)
                        AddConstraint(relationName, add.Groups["definition"].Value, inline: false);
                }
                continue;
            }
            var match = ObjectDefinition.Match(statement);
            if (!match.Success)
                continue;
            var kind = match.Groups["kind"].Value.ToUpperInvariant();
            var name = Sql.Unquote(match.Groups["name"].Value);
            var triggerTable = kind == "TRIGGER" ? TriggerTable.Match(statement[match.Length..]) : Match.Empty;
            var owner = triggerTable.Success ? Sql.Unquote(triggerTable.Groups["table"].Value) : null;
            if (selection.IncludesObject(name, owner))
                objects.Add(new Definition(kind, name, owner, "CREATE OR ALTER " + statement[match.Groups["kind"].Index..]));
            else if (selection.Objects.Contains(name))
            {
                var label = $"{kind} {name}";
                if (!omitted.Contains(label, StringComparer.Ordinal))
                    omitted.Add(label);
            }
            else
                omitted.Add($"{kind} {name}, tabela {owner}");
        }
        return new MetadataPlan(primary, foreign, objects, omitted);

        void AddConstraint(string table, string definition, bool inline)
        {
            var key = ReadConstraint(table, definition, inline);
            if (key is null)
                return;
            if (selection.IncludesKey(table, key.Reference, key.Name))
                (key.Primary ? primary : foreign).Add(key);
            else
                omitted.Add($"{(key.Primary ? "PK" : "FK")} {key.Name ?? "(sem nome)"}, tabela {table}");
        }
    }

    private static string Render(MetadataPlan plan)
    {
        var objects = plan.Objects;
        var allSql = objects.Select(item => item.Sql).Concat(plan.Primary.Concat(plan.Foreign).Select(key => key.Definition)).ToArray();
        var delimiter = "^";
        while (allSql.Any(sql => sql.Contains(delimiter, StringComparison.Ordinal)))
            delimiter += "^";
        var script = new StringBuilder();
        script.AppendLine("-- Firebird 5. Conecte o isql ao banco restaurado com -ch UTF8 antes de executar este arquivo.");
        script.AppendLine("-- Requer as tabelas, os domínios, as sequências e as demais dependências já existentes no destino.");
        script.AppendLine("-- PK já presente e FK com o mesmo nome são preservadas. Chaves em tabelas ausentes são ignoradas.");
        script.AppendLine("-- Em caso de erro, revise a mensagem e faça ROLLBACK se a sessão permanecer aberta.");
        var declaredExceptions = objects.Where(item => item.Kind == "EXCEPTION").Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var exceptionReferences = new Regex($@"\bEXCEPTION\s+(?<name>{Identifier})(?=\s*(?:;|USING\b|DO\b))",
            RegexOptions.IgnoreCase);
        foreach (var item in objects.Where(item => item.Kind is "TRIGGER" or "PROCEDURE"))
        {
            var external = exceptionReferences.Matches(MaskQuotedComments(item.Sql))
                .Select(reference => Sql.Unquote(reference.Groups["name"].Value))
                .Where(name => !declaredExceptions.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
            if (external.Length != 0)
                script.AppendLine($"-- {item.Kind} {OneLine(item.Name)} requer exceção existente no destino: {OneLine(string.Join(", ", external))}");
        }
        foreach (var item in plan.Omitted)
            script.AppendLine("-- Excluído: " + OneLine(item));
        script.AppendLine("SET SQL DIALECT 3;");
        script.AppendLine("SET BAIL ON;");
        script.AppendLine("SET AUTODDL OFF;");
        script.AppendLine($"SET TERM {delimiter} ;");
        script.AppendLine();
        AppendObjects(script, delimiter, objects.Where(item => item.Kind == "EXCEPTION"));
        AppendKeys(script, delimiter, plan.Primary);
        AppendKeys(script, delimiter, plan.Foreign);
        AppendObjects(script, delimiter, objects.Where(item => item.Kind != "EXCEPTION"));
        script.AppendLine($"SET TERM ; {delimiter}");
        script.AppendLine("COMMIT;");
        return script.ToString();
    }

    private static Constraint? ReadConstraint(string table, string definition, bool inline)
    {
        definition = TrimLeading(definition);
        var match = Key.Match(definition);
        if (!match.Success && inline)
        {
            var field = Regex.Match(definition, $@"^(?<field>{Identifier})\s+(?<type>.+)$", RegexOptions.Singleline);
            if (!field.Success)
                return null;
            var type = field.Groups["type"].Value;
            var masked = MaskQuotedComments(type);
            var columnKey = Regex.Match(masked, $@"\b(?:(?:CONSTRAINT\s+(?<name>{Identifier}))\s+)?(?<kind>PRIMARY\s+KEY|REFERENCES)\b", RegexOptions.IgnoreCase);
            if (!columnKey.Success)
                return null;
            var column = field.Groups["field"].Value;
            var constraintName = columnKey.Groups["name"];
            var prefix = constraintName.Success ? $"CONSTRAINT {type.Substring(constraintName.Index, constraintName.Length)} " : "";
            definition = columnKey.Groups["kind"].Value.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase)
                ? $"{prefix}PRIMARY KEY ({column})"
                : $"{prefix}FOREIGN KEY ({column}) {type[columnKey.Groups["kind"].Index..]}";
            match = Key.Match(definition);
        }
        if (!match.Success)
            return null;
        var isPrimary = match.Groups["kind"].Value.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase);
        var reference = isPrimary ? Match.Empty : TargetTable.Match(definition);
        var target = reference.Success ? Sql.Unquote(reference.Groups["table"].Value) : null;
        var name = match.Groups["name"].Success ? Sql.Unquote(match.Groups["name"].Value) : null;
        return new Constraint(table, target, name, definition, isPrimary);
    }

    private static void AppendKeys(StringBuilder script, string delimiter, IEnumerable<Constraint> keys)
    {
        foreach (var key in keys)
        {
            var relation = Sql.Literal(key.Table);
            var type = key.Primary ? "PRIMARY KEY" : "FOREIGN KEY";
            var existing = !key.Primary && key.Name is null ? ""
                : $" AND NOT EXISTS (SELECT 1 FROM RDB$RELATION_CONSTRAINTS WHERE RDB$RELATION_NAME = {relation} AND RDB$CONSTRAINT_TYPE = '{type}'"
                  + (!key.Primary ? $" AND RDB$CONSTRAINT_NAME = {Sql.Literal(key.Name!)}" : "") + ")";
            var reference = key.Reference is null ? "" : " AND " + RelationExists(key.Reference);
            var ddl = $"ALTER TABLE {Sql.Name(key.Table)} ADD {key.Definition}";
            script.AppendLine("EXECUTE BLOCK AS");
            script.AppendLine("BEGIN");
            script.AppendLine($"  IF ({RelationExists(key.Table)}{reference}{existing}) THEN");
            script.AppendLine($"    EXECUTE STATEMENT {Sql.Literal(ddl)};");
            script.AppendLine($"END{delimiter}");
        }
    }

    private static void AppendObjects(StringBuilder script, string delimiter, IEnumerable<Definition> definitions)
    {
        foreach (var item in definitions)
        {
            if (item.Table is null)
                script.AppendLine(item.Sql + "\n" + delimiter);
            else
            {
                if (Encoding.UTF8.GetByteCount(item.Sql) > 32000)
                    throw new InvalidOperationException($"O gatilho {item.Name} excede o tamanho que a proteção de tabela ausente suporta.");
                script.AppendLine("EXECUTE BLOCK AS");
                script.AppendLine("BEGIN");
                script.AppendLine($"  IF ({RelationExists(item.Table)}) THEN");
                script.AppendLine($"    EXECUTE STATEMENT {Sql.Literal(item.Sql)};");
                script.AppendLine($"END{delimiter}");
            }
        }
    }

    private static string RelationExists(string name) =>
        $"EXISTS (SELECT 1 FROM RDB$RELATIONS WHERE RDB$RELATION_NAME = {Sql.Literal(name)})";

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');


    private static string TrimLeading(string text) =>
        Regex.Replace(text, """\A(?:\s+|/\*[\s\S]*?\*/|--[^\r\n]*)*""", "");

    private static string MaskQuotedComments(string text) =>
        Lexeme.Replace(text, match => match.Value.StartsWith('"') || match.Length == 1 ? match.Value : new string(' ', match.Length));

    private static IEnumerable<string> TableItems(string source)
    {
        var depth = 0;
        var start = 1;
        foreach (Match match in Lexeme.Matches(source))
        {
            switch (match.Value)
            {
                case "(": depth++; break;
                case ")":
                    if (--depth == 0)
                    {
                        yield return source[start..match.Index].Trim();
                        yield break;
                    }
                    break;
                case "," when depth == 1:
                    yield return source[start..match.Index].Trim();
                    start = match.Index + 1;
                    break;
            }
        }
    }

    private static IEnumerable<string> ReadStatements(string source)
    {
        var delimiter = ";";
        var start = 0;
        var index = 0;
        using var tokens = Lexeme.Matches(source).Cast<Match>().GetEnumerator();
        var hasToken = tokens.MoveNext();
        while (index < source.Length)
        {
            if (hasToken && tokens.Current.Index == index)
            {
                var token = tokens.Current;
                hasToken = tokens.MoveNext();
                if (token.Length > 1 || token.Value is "'" or "\"")
                {
                    index += token.Length;
                    continue;
                }
            }
            if (!source.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal))
            {
                index++;
                continue;
            }
            var statement = TrimLeading(source[start..index]).TrimEnd();
            index += delimiter.Length;
            start = index;
            while (hasToken && tokens.Current.Index < index)
                hasToken = tokens.MoveNext();
            var term = Regex.Match(statement, @"^SET\s+TERM\s+(\S+)$", RegexOptions.IgnoreCase);
            if (term.Success)
                delimiter = term.Groups[1].Value;
            else if (statement.Length != 0)
                yield return statement;
        }
        if (TrimLeading(source[start..]).Trim().Length != 0)
            throw new InvalidOperationException($"O SQL termina com uma instrução incompleta. Delimitador esperado: {delimiter}");
    }
}
