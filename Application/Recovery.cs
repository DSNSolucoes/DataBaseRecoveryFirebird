using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Application;

public sealed record RecoveryOptions(string Source, string Destination, string Firebird, ExcludedNames Excluded);
public sealed record RecoveryResult(string Folder, int Tables, long Rows, bool Verified, int Unrecovered);

public sealed class RecoverDatabase(IWorkspaceFiles files, IFirebirdTools tools)
{
    public Task<RecoveryResult> ExecuteAsync(RecoveryOptions options, Action<string> progress, CancellationToken cancellation) =>
        new RecoveryExecution(options, progress, files, tools).RunAsync(cancellation);
}

internal sealed class RecoveryExecution(RecoveryOptions options, Action<string> progress, IWorkspaceFiles files, IFirebirdTools tools)
{
    private sealed record OriginSchema(string Charset, HashSet<string> InitialTables, HashSet<string> KnownTables,
        List<string> Definitions, List<string> Generators, bool ExtractionComplete);

    private static readonly Regex Relation = new(@"(?im)^.*?Relation \d+ \(([^)]+)\)(?:\s+is ok)?\s*$");
    private string folder = "";
    private string source = "";
    private string target = "";

    public async Task<RecoveryResult> RunAsync(CancellationToken cancellation)
    {
        var input = Path.GetFullPath(options.Source);
        if (!files.FileExists(input))
            throw new FileNotFoundException("Selecione um arquivo de banco de dados existente.");
        if (!files.DirectoryExists(options.Destination))
            throw new DirectoryNotFoundException("Selecione uma pasta de saída existente.");
        tools.ValidateInstallation(options.Firebird);

        var runId = Guid.NewGuid().ToString("N")[..8];
        folder = Path.Combine(options.Destination, $"recuperacao-{DateTime.Now:yyyyMMdd-HHmmss}-{runId}");
        files.CreateDirectory(folder);
        source = Path.Combine(folder, "origem-somente-leitura.fdb");
        target = Path.Combine(folder, "banco-recuperado.fdb");
        progress($"Arquivos da execução em {folder}");
        try
        {
            return await RecoverAsync(input, cancellation);
        }
        catch (Exception exception)
        {
            await Save("resumo.txt", $"Execução {(exception is OperationCanceledException ? "cancelada" : "interrompida")}.\n{exception.Message}\nPasta: {folder}\n");
            throw;
        }
    }

    private async Task<RecoveryResult> RecoverAsync(string input, CancellationToken cancellation)
    {
        await PrepareSource(input, cancellation);
        var schema = await InspectSchema(cancellation);
        var supplemental = await ExtractMissingDefinitions(schema, cancellation);
        await CreateDestination(schema, supplemental, cancellation);
        var tables = await ReadDestinationColumns(cancellation);
        var counts = await TransferData(schema.InitialTables, tables, cancellation);
        await CopyGenerators(schema.Generators, cancellation);
        return await VerifyAndReport(input, schema, tables.Keys, counts, cancellation);
    }

    private async Task PrepareSource(string input, CancellationToken cancellation)
    {
        progress("Copiando o arquivo original...");
        await files.CopyExclusiveAsync(input, source, cancellation);

        var header = await Run("gstat", "cabecalho.txt", cancellation, "-h", source);
        var ods = Regex.Match(header.Output, @"(?im)^\s*ODS version\s+(\S+)");
        if (ods.Success && ods.Groups[1].Value != "13.1")
            progress($"ODS da origem: {ods.Groups[1].Value}. A instalação selecionada precisa conseguir abrir essa versão.");
        var readOnly = await Run("gfix", "modo-somente-leitura.log", cancellation, "-mode", "read_only", "-user", "SYSDBA", source);
        if (!readOnly.Ok)
            throw new InvalidOperationException("O Firebird não conseguiu abrir a cópia em modo somente leitura. Consulte modo-somente-leitura.log.");
    }

    private async Task<OriginSchema> InspectSchema(CancellationToken cancellation)
    {
        progress("Extraindo domínios, sequências e tabelas...");
        var extracted = await Run("isql", "extracao-esquema.log", cancellation, "-x", "-nodbtriggers", "-ch", "UTF8",
            "-user", "SYSDBA", "-o", Path.Combine(folder, "esquema-extraido.sql"), source);
        var extractedPath = Path.Combine(folder, "esquema-extraido.sql");
        var ddl = files.FileExists(extractedPath) ? await files.ReadTextAsync(extractedPath, cancellation) : "";
        var charset = Regex.Match(ddl, @"(?i)DEFAULT CHARACTER SET ([A-Z][A-Z0-9_]*);");
        var databaseCharset = charset.Success ? charset.Groups[1].Value : "NONE";
        if (!charset.Success)
            progress("O esquema extraído não informou o conjunto de caracteres padrão. A criação usará NONE.");
        var initialTables = new HashSet<string>(StringComparer.Ordinal);
        var knownTables = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<string>();
        var generators = new List<string>();
        foreach (var statement in Sql.Statements(ddl))
        {
            var table = Sql.Table.Match(statement);
            var generator = Sql.Generator.Match(statement);
            if (table.Success)
            {
                var name = Sql.Unquote(table.Groups["name"].Value);
                if (!table.Groups["temporary"].Success)
                    knownTables.Add(name);
                if (options.Excluded.Contains(name))
                    continue;
                initialTables.Add(name);
                definitions.Add(statement);
            }
            else if (generator.Success)
            {
                generators.Add(Sql.Unquote(generator.Groups["name"].Value));
                definitions.Add(statement);
            }
            else if (Regex.IsMatch(statement, @"^CREATE\s+(DOMAIN|COLLATION)\s+", RegexOptions.IgnoreCase))
                definitions.Add(statement);
        }

        if (!extracted.Ok)
        {
            progress("A extração do catálogo foi parcial. Procurando outras tabelas pelo serviço de validação...");
            var inventory = await Run("fbsvcmgr", "validacao-origem-por-tabela.log", cancellation,
                "service_mgr", "user", "SYSDBA", "action_validate", "dbname", source);
            foreach (Match relation in Relation.Matches(inventory.Output))
                knownTables.Add(relation.Groups[1].Value);
            if (!inventory.Ok)
                progress("A validação da origem também retornou erros. Consulte o log de inventário.");
        }
        return new OriginSchema(databaseCharset, initialTables, knownTables, definitions, generators, extracted.Ok);
    }

    private async Task<List<string>> ExtractMissingDefinitions(OriginSchema schema, CancellationToken cancellation)
    {
        var supplemental = new List<string>();
        var missing = schema.KnownTables.Where(name => !schema.InitialTables.Contains(name) && !options.Excluded.Contains(name))
            .Order(StringComparer.Ordinal).ToArray();
        var showLog = new StringBuilder();
        foreach (var table in missing)
        {
            var show = await Query($"SHOW TABLE {Sql.Name(table)};\nQUIT;", "mostrar-tabela.sql", null, source, cancellation);
            showLog.AppendLine($"--- {table} ---").AppendLine(show.Output);
            var definition = show.Ok ? Sql.FromShowTable(table, show.Output) : null;
            if (definition is not null)
                supplemental.Add(definition);
            else
                progress($"Estrutura indisponível para {table}.");
        }
        await Save("estruturas-complementares.log", showLog.ToString());
        return supplemental;
    }

    private async Task CreateDestination(OriginSchema schema, List<string> supplemental, CancellationToken cancellation)
    {
        if (schema.Definitions.Count + supplemental.Count == 0)
            throw new InvalidOperationException("Nenhuma estrutura de tabela foi recuperada.");

        progress("Criando o banco de destino...");
        var start = $"SET SQL DIALECT 3;\nSET NAMES UTF8;\nCREATE DATABASE {Sql.Literal(target)} USER 'SYSDBA' PAGE_SIZE 8192 DEFAULT CHARACTER SET {schema.Charset};\n";
        var created = await Query(start + Script(schema.Definitions), "criar-banco.sql", "criar-banco.log", null, cancellation, true);
        if (!created.Ok || !files.FileExists(target))
            throw new InvalidOperationException("Falha ao criar o banco novo. Consulte criar-banco.log.");
        await Query(Script(supplemental), "criar-tabelas-complementares.sql", "criar-tabelas-complementares.log", target, cancellation);
    }

    private async Task<Dictionary<string, string[]>> ReadDestinationColumns(CancellationToken cancellation)
    {
        var columnsResult = await Query("""
            SET HEADING OFF;
            SELECT TRIM(rf.RDB$RELATION_NAME) || '|' || TRIM(rf.RDB$FIELD_NAME)
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = rf.RDB$RELATION_NAME
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            WHERE r.RDB$SYSTEM_FLAG = 0 AND r.RDB$RELATION_TYPE = 0
            AND f.RDB$COMPUTED_BLR IS NULL
            ORDER BY rf.RDB$RELATION_NAME, rf.RDB$FIELD_POSITION;
            QUIT;
            """, "colunas-destino.sql", "colunas-destino.log", target, cancellation, true);
        if (!columnsResult.Ok)
            throw new InvalidOperationException("Falha ao consultar as colunas do banco novo.");
        return columnsResult.Output.Split('\n').Select(line => line.Trim().Split('|', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(parts => parts[1]).ToArray(), StringComparer.Ordinal);
    }

    private async Task<Dictionary<string, long>> TransferData(IReadOnlySet<string> initialTables,
        IReadOnlyDictionary<string, string[]> tables, CancellationToken cancellation)
    {
        progress($"Transferindo dados de {tables.Count} tabelas...");
        const string preamble = "SET LIST ON;\nSET TERM ^;\n";
        const string ending = "\nSET TERM ;^\nQUIT;\n";
        var mainSql = preamble + string.Concat(tables.Where(pair => initialTables.Contains(pair.Key))
            .Select(pair => Sql.CopyTable(source, pair.Key, pair.Value))) + ending;
        var supplementarySql = preamble + string.Concat(tables.Where(pair => !initialTables.Contains(pair.Key))
            .Select(pair => Sql.CopyTable(source, pair.Key, pair.Value))) + ending;
        var copied = await Query(mainSql, "copiar-dados.sql", "copiar-dados.log", target, cancellation, onLine: CopyProgress);
        var copiedMore = await Query(supplementarySql, "copiar-tabelas-complementares.sql",
            "copiar-tabelas-complementares.log", target, cancellation, onLine: CopyProgress);
        return ReadTransferCounts(copied.Output + "\n" + copiedMore.Output);
    }

    private async Task<RecoveryResult> VerifyAndReport(string input, OriginSchema schema,
        IEnumerable<string> tables, IReadOnlyDictionary<string, long> counts, CancellationToken cancellation)
    {
        progress("Conferindo a contagem no banco novo...");
        var queryCounts = "SET HEADING OFF;\n" + string.Join("\n", tables.Select(table =>
            $"SELECT {Sql.Literal(table + "|")} || CAST(COUNT(*) AS VARCHAR(25)) FROM {Sql.Name(table)};")) + "\nQUIT;";
        var checkedTables = await Query(queryCounts, "verificar-banco.sql", "verificar-banco.log", target, cancellation);
        var checkedCounts = ReadPairs(checkedTables.Output);
        var reconciliation = new RecoveryReconciliation(schema.KnownTables.Concat(tables), counts, checkedCounts, options.Excluded);
        var report = new StringBuilder("Tabela;Transferidos;NoBancoNovo;Conferido\n");
        foreach (var row in reconciliation.Tables)
        {
            report.AppendLine($"\"{row.Table.Replace("\"", "\"\"")}\";{row.Transferred};{row.InDestination};{(row.Matches ? "SIM" : "NÃO")}");
        }
        await files.WriteTextAsync(Path.Combine(folder, "conferencia-dados.csv"), report.ToString(), withBom: true, cancellation);

        progress("Validando a estrutura do banco novo...");
        var validation = await Run("gfix", "validacao-banco-novo.log", cancellation,
            "-validate", "-full", "-no_update", "-user", "SYSDBA", target);
        var verified = validation.Ok && string.IsNullOrWhiteSpace(validation.Output) && checkedTables.Ok && reconciliation.UnverifiedTables == 0;
        var result = new RecoveryResult(folder, reconciliation.TransferredTables, reconciliation.TransferredRows, verified, reconciliation.UnverifiedTables);
        await Save("resumo.txt", $"""
            Banco recuperado: {target}
            Origem: {input}
            Tabelas transferidas: {result.Tables}
            Registros transferidos: {result.Rows}
            Tabelas conhecidas sem conferência: {result.Unrecovered}
            Excluídas: {string.Join(", ", options.Excluded.Ordered())}
            Extração automática do esquema: {(schema.ExtractionComplete ? "concluída" : "parcial")}
            Validação estrutural e contagens: {(verified ? "sem divergências detectadas" : "revisar os logs")}

            A recuperação usa consultas às tabelas legíveis. O relatório valida contagens dos registros transferidos.
            Gatilhos, visões, procedimentos, permissões e restrições fora das definições recriadas requerem revisão.
            A corrupção pode ocultar tabelas ou registros que não chegaram aos relatórios.
            """);
        return result;
    }

    private async Task CopyGenerators(List<string> generators, CancellationToken cancellation)
    {
        if (generators.Count == 0)
            return;
        var sql = "SET HEADING OFF;\n" + string.Join("\n", generators.Select(name =>
            $"SELECT {Sql.Literal(name + "|")} || CAST(GEN_ID({Sql.Name(name)}, 0) AS VARCHAR(25)) FROM RDB$DATABASE;")) + "\nQUIT;";
        var result = await Query(sql, "ler-geradores.sql", "ler-geradores.log", source, cancellation);
        var values = ReadPairs(result.Output);
        var restored = await Query(Script(values.Select(pair =>
            $"SET GENERATOR {Sql.Name(pair.Key)} TO {pair.Value.ToString(CultureInfo.InvariantCulture)}")),
            "restaurar-geradores.sql", "restaurar-geradores.log", target, cancellation);
        progress($"Valores de geradores lidos: {values.Count} de {generators.Count}. {(restored.Ok ? "" : "Revise o log de restauração.")}".TrimEnd());
    }

    private void CopyProgress(string line)
    {
        if (line.StartsWith("TRANSFERINDO", StringComparison.Ordinal))
            progress("Transferindo " + line["TRANSFERINDO".Length..].Trim());
        else if (line.StartsWith("Statement failed", StringComparison.Ordinal))
            progress(line.Trim());
    }

    private static Dictionary<string, long> ReadTransferCounts(string output)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var table = "";
        foreach (var line in output.Split('\n'))
        {
            var marker = Regex.Match(line, @"^TRANSFERINDO\s+(.+?)\s*$");
            if (marker.Success)
                table = marker.Groups[1].Value;
            var number = Regex.Match(line, @"^LINHAS\s+(\d+)\s*$");
            if (number.Success && table.Length > 0)
                counts[table] = long.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture);
            if (line.StartsWith("Statement failed", StringComparison.Ordinal) && table.Length > 0)
                counts.Remove(table);
        }
        return counts;
    }

    private static Dictionary<string, long> ReadPairs(string output)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (Match pair in Regex.Matches(output, @"(?m)^[ \t]*([^|\r\n]+)\|(-?\d+)[ \t\r]*$"))
            result[pair.Groups[1].Value.Trim()] = long.Parse(pair.Groups[2].Value, CultureInfo.InvariantCulture);
        return result;
    }

    private static string Script(IEnumerable<string> statements) =>
        string.Concat(statements.Select(statement => statement + ";\n")) + "COMMIT;\nQUIT;\n";

    private Task Save(string name, string content) => files.WriteTextAsync(Path.Combine(folder, name), content);

    private async Task<FirebirdCommandResult> Query(string sql, string script, string? log, string? database,
        CancellationToken cancellation, bool bail = false, Action<string>? onLine = null)
    {
        await Save(script, sql);
        var args = new List<string> { "-nodbtriggers", "-ch", "UTF8", "-user", "SYSDBA", "-i", Path.Combine(folder, script) };
        if (bail)
            args.Insert(0, "-bail");
        if (database is not null)
            args.Add(database);
        return await RunTool("isql", log, cancellation, args, onLine);
    }

    private Task<FirebirdCommandResult> Run(string tool, string log, CancellationToken cancellation, params string[] args) =>
        RunTool(tool, log, cancellation, args);

    private Task<FirebirdCommandResult> RunTool(string tool, string? log, CancellationToken cancellation,
        IReadOnlyList<string> args, Action<string>? onLine = null) =>
        tools.RunAsync(options.Firebird, tool, args, log is null ? null : Path.Combine(folder, log), onLine, cancellation);
}
