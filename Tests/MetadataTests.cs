using System.Text;
using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Tests;

public sealed class MetadataTests
{
    private static readonly SchemaSelection AllObjects = new(ExcludedNames.Parse(""), ExcludedNames.Parse(""));

    [Fact]
    public void ComposerExtractsOnlyRequestedObjectsAndPreservesPsql()
    {
        const string input = """
            CREATE DATABASE 'outer.fdb';
            CREATE DOMAIN D_ID INTEGER;
            CREATE TABLE PARENT (ID INTEGER CONSTRAINT PK_P PRIMARY KEY);
            CREATE TABLE CHILD (ID INTEGER PRIMARY KEY, PARENT_ID INTEGER REFERENCES PARENT (ID));
            ALTER TABLE CHILD ADD FLAG INTEGER, ADD CONSTRAINT FK_OTHER FOREIGN KEY (ID) REFERENCES PARENT (ID);
            INSERT INTO CHILD VALUES (1, 2);
            CREATE EXCEPTION EX_BAD 'Conteúdo; ^';
            SET TERM !! ;
            CREATE PROCEDURE PROC_TEST RETURNS (N INTEGER) AS
            BEGIN
              N = 7;
              SUSPEND;
            END!!
            CREATE TRIGGER TR_CHILD FOR CHILD BEFORE INSERT AS
            BEGIN
              IF (NEW.ID < 0) THEN EXCEPTION EX_BAD 'd''água; !!';
            END!!
            SET TERM ; !!
            CREATE VIEW V_CHILD AS SELECT ID FROM CHILD;
            """;

        var result = MetadataScriptComposer.Compose(input, AllObjects);

        Assert.Equal(new MetadataResult(2, 2, 1, 1, 1, 1, 0), result.Counts);
        Assert.Contains("SET TERM ^^ ;", result.Content);
        Assert.Contains("d''''água; !!", result.Content);
        Assert.Contains("ALTER TABLE \"CHILD\" ADD FOREIGN KEY (PARENT_ID) REFERENCES PARENT (ID)", result.Content);
        Assert.DoesNotContain("CREATE TABLE", result.Content);
        Assert.DoesNotContain("CREATE DATABASE", result.Content);
        Assert.DoesNotContain("CREATE DOMAIN", result.Content);
        Assert.DoesNotContain("ADD FLAG", result.Content);
        Assert.DoesNotContain("INSERT INTO CHILD", result.Content);
        Assert.True(result.Content.IndexOf("EXCEPTION EX_BAD", StringComparison.Ordinal) <
            result.Content.IndexOf("CREATE OR ALTER TRIGGER", StringComparison.Ordinal));
        Assert.True(result.Content.IndexOf("ADD CONSTRAINT PK_P", StringComparison.Ordinal) <
            result.Content.IndexOf("ADD FOREIGN KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void ComposerReportsDependenciesAndOmitsAllDefinitionsOfExcludedProcedure()
    {
        const string input = """
            ALTER TABLE CHILD ADD CONSTRAINT FK_CHILD FOREIGN KEY (PARENT_ID) REFERENCES OMITTED (ID);
            SET TERM ^ ;
            CREATE PROCEDURE P_OMIT AS BEGIN EXIT; END^
            ALTER PROCEDURE P_OMIT AS BEGIN EXIT; END^
            CREATE TRIGGER TR_KEEP FOR CHILD BEFORE INSERT AS BEGIN EXCEPTION EX_EXTERNAL 'mensagem'; END^
            SET TERM ; ^
            """;

        var selection = new SchemaSelection(ExcludedNames.Parse("OMITTED"), ExcludedNames.Parse("P_OMIT"));
        var result = MetadataScriptComposer.Compose(input, selection);

        Assert.Equal(new MetadataResult(0, 0, 1, 0, 0, 0, 2), result.Counts);
        Assert.Contains("-- TRIGGER TR_KEEP requer exceção existente no destino: EX_EXTERNAL", result.Content);
        Assert.Contains("-- Excluído: PROCEDURE P_OMIT", result.Content);
        Assert.DoesNotContain("CREATE OR ALTER PROCEDURE", result.Content);
        Assert.DoesNotContain("ADD CONSTRAINT FK_CHILD", result.Content);
    }

    [Fact]
    public void ComposerRejectsIncompletePsql()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MetadataScriptComposer.Compose("SET TERM ^ ; CREATE PROCEDURE P AS BEGIN EXIT; END", AllObjects));

        Assert.Contains("Delimitador esperado: ^", error.Message);
    }

    [Fact]
    public async Task UseCaseReadsWindows1252AndWritesUtf8()
    {
        var files = new MemoryFiles();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        files.SetBytes("meta.sql", Encoding.GetEncoding(1252).GetBytes("CREATE EXCEPTION EX_T 'Atenção €';"));

        var counts = await new GenerateMetadataScript(files).ExecuteAsync("meta.sql", "out.sql", AllObjects);

        Assert.Equal(1, counts.Exceptions);
        Assert.Contains("Atenção €", await files.ReadTextAsync("out.sql", CancellationToken.None));
    }

    [Fact]
    public async Task UseCaseRejectsOverwritingInput()
    {
        var files = new MemoryFiles();
        const string original = "CREATE EXCEPTION EX_T 'alerta';";
        await files.WriteTextAsync("meta.sql", original);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GenerateMetadataScript(files).ExecuteAsync("meta.sql", "./meta.sql", AllObjects));

        Assert.Equal(original, await files.ReadTextAsync("meta.sql", CancellationToken.None));
    }

    [Fact]
    public async Task UseCaseReadsUtf16WithByteOrderMark()
    {
        var files = new MemoryFiles();
        var bytes = Encoding.Unicode.GetPreamble().Concat(
            Encoding.Unicode.GetBytes("CREATE EXCEPTION EX_T 'Informação';")).ToArray();
        files.SetBytes("meta.sql", bytes);

        await new GenerateMetadataScript(files).ExecuteAsync("meta.sql", "out.sql", AllObjects);

        Assert.Contains("Informação", await files.ReadTextAsync("out.sql", CancellationToken.None));
    }
}
