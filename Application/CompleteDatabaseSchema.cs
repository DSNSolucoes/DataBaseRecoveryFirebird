namespace RecuperadorFirebird.Application;

public sealed record SchemaCompletionRequest(string Metadata, string Database, string Firebird);

public sealed class CompleteDatabaseSchema(IWorkspaceFiles files, IFirebirdTools tools)
{
    private const string Inventory = """
        SET BAIL ON;
        SET HEADING OFF;
        SET TRANSACTION READ ONLY;
        SELECT 'T|' || TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS
          WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 AND RDB$VIEW_BLR IS NULL;
        SELECT 'C|' || TRIM(f.RDB$RELATION_NAME) || '|' || TRIM(f.RDB$FIELD_NAME)
          FROM RDB$RELATION_FIELDS f JOIN RDB$RELATIONS r ON r.RDB$RELATION_NAME = f.RDB$RELATION_NAME
          WHERE COALESCE(r.RDB$SYSTEM_FLAG, 0) = 0 AND r.RDB$VIEW_BLR IS NULL;
        SELECT 'O|VIEW|' || TRIM(RDB$RELATION_NAME) FROM RDB$RELATIONS
          WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 AND RDB$VIEW_BLR IS NOT NULL;
        SELECT 'O|TRIGGER|' || TRIM(RDB$TRIGGER_NAME) FROM RDB$TRIGGERS WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0;
        SELECT 'O|PROCEDURE|' || TRIM(RDB$PROCEDURE_NAME) FROM RDB$PROCEDURES
          WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0 AND RDB$PACKAGE_NAME IS NULL;
        SELECT 'O|EXCEPTION|' || TRIM(RDB$EXCEPTION_NAME) FROM RDB$EXCEPTIONS
          WHERE COALESCE(RDB$SYSTEM_FLAG, 0) = 0;
        SELECT 'K|' || TRIM(c.RDB$RELATION_NAME) || '|' ||
          CASE c.RDB$CONSTRAINT_TYPE WHEN 'PRIMARY KEY' THEN 'PK' WHEN 'FOREIGN KEY' THEN 'FK' ELSE 'UNQ' END ||
          '|' || TRIM(c.RDB$CONSTRAINT_NAME) || '|' || TRIM(s.RDB$FIELD_NAME) || '|' ||
          COALESCE(TRIM(p.RDB$RELATION_NAME), '') || '|' || COALESCE(TRIM(ps.RDB$FIELD_NAME), '')
          FROM RDB$RELATION_CONSTRAINTS c
          JOIN RDB$INDEX_SEGMENTS s ON s.RDB$INDEX_NAME = c.RDB$INDEX_NAME
          LEFT JOIN RDB$REF_CONSTRAINTS ref ON ref.RDB$CONSTRAINT_NAME = c.RDB$CONSTRAINT_NAME
          LEFT JOIN RDB$RELATION_CONSTRAINTS p ON p.RDB$CONSTRAINT_NAME = ref.RDB$CONST_NAME_UQ
          LEFT JOIN RDB$INDEX_SEGMENTS ps ON ps.RDB$INDEX_NAME = p.RDB$INDEX_NAME AND ps.RDB$FIELD_POSITION = s.RDB$FIELD_POSITION
          WHERE c.RDB$CONSTRAINT_TYPE IN ('PRIMARY KEY', 'FOREIGN KEY', 'UNIQUE')
          ORDER BY c.RDB$RELATION_NAME, c.RDB$CONSTRAINT_NAME, s.RDB$FIELD_POSITION;
        SELECT 'FIM|CATALOGO' FROM RDB$DATABASE;
        ROLLBACK;
        QUIT;
        """;

    public async Task<IReadOnlyList<SchemaChange>> AnalyzeAsync(SchemaCompletionRequest request, CancellationToken cancellation = default)
    {
        if (!files.FileExists(request.Metadata) || !files.FileExists(request.Database))
            throw new FileNotFoundException("Selecione o arquivo de metadados e o banco recuperado.");
        if (Path.GetFullPath(request.Metadata).Equals(Path.GetFullPath(request.Database), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selecione arquivos diferentes para metadados e banco.");
        tools.ValidateInstallation(request.Firebird);
        var catalog = await Execute(request, Inventory, cancellation);
        if (!catalog.Split('\n').Any(line => line.Trim() == "FIM|CATALOGO"))
            throw new InvalidOperationException("O Firebird não retornou o catálogo completo.");
        return SchemaCompletion.Compare(GenerateMetadataScript.Decode(files.ReadBytes(request.Metadata)), catalog);
    }

    public async Task<IReadOnlyList<SchemaChange>> ApplyAsync(SchemaCompletionRequest request, IReadOnlyList<SchemaChange> reviewed)
    {
        var current = await AnalyzeAsync(request);
        if (!current.SequenceEqual(reviewed))
            throw new InvalidOperationException("O esquema ou o SQL mudou após a análise. Analise novamente.");
        if (current.Any(change => change.BlockedReason is null))
            await Execute(request, SchemaCompletion.BuildScript(current), CancellationToken.None);
        return await AnalyzeAsync(request);
    }

    private async Task<string> Execute(SchemaCompletionRequest request, string sql, CancellationToken cancellation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"firebird-esquema-{Guid.NewGuid():N}.sql");
        try
        {
            await files.WriteTextAsync(path, sql, cancellation: cancellation);
            var result = await tools.RunAsync(request.Firebird, "isql",
                ["-bail", "-nodbtriggers", "-ch", "UTF8", "-user", "SYSDBA", "-i", path, request.Database],
                null, null, cancellation);
            if (!result.Ok)
                throw new InvalidOperationException(result.Output.Trim().Length == 0
                    ? $"O isql terminou com código {result.ExitCode}." : result.Output.Trim());
            return result.Output;
        }
        finally
        {
            files.DeleteFile(path);
        }
    }
}
