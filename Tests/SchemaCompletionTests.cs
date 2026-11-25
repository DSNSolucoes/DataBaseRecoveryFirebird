using RecuperadorFirebird.Application;

namespace RecuperadorFirebird.Tests;

public sealed class SchemaCompletionTests
{
    [Fact]
    public void PlansMissingTablesFieldsAndKeysWithoutRecreatingEquivalentPrimaryKey()
    {
        const string meta = """
            CREATE TABLE P (ID INTEGER PRIMARY KEY, TITLE VARCHAR(20));
            ALTER TABLE P ADD COLUMN CODE INTEGER, ADD CONSTRAINT UQ_P UNIQUE (CODE);
            CREATE TABLE C (ID INTEGER PRIMARY KEY, PID INTEGER REFERENCES P (ID));
            """;
        const string catalog = "T|P\nC|P|ID\nK|P|PK|INTEG_1|ID||";
        var plan = SchemaCompletion.Compare(meta, catalog);

        Assert.Equal(new[] { "Tabela", "Campo", "Campo", "PK", "UNQ", "FK" }, plan.Select(change => change.Kind));
        Assert.DoesNotContain(plan, change => change.Statement.Contains("CREATE TABLE P"));
        Assert.Contains(plan, change => change.Name == "P.CODE");
        Assert.Contains(plan, change => change.Name == "P.TITLE");
    }

    [Fact]
    public void BlocksBothProcedureDefinitionsUntilReferencedExceptionExists()
    {
        const string meta = """
            SET TERM ^ ;
            CREATE PROCEDURE P AS BEGIN EXIT; END^
            ALTER PROCEDURE P AS BEGIN EXCEPTION EX_ABSENT 'missing'; END^
            SET TERM ; ^
            CREATE VIEW V AS SELECT 1 AS ID FROM RDB$DATABASE;
            """;

        var plan = SchemaCompletion.Compare(meta, "");
        Assert.Equal(2, plan.Count(change => change.BlockedReason == "Exceção ausente: EX_ABSENT"));
        Assert.DoesNotContain("PROCEDURE P", SchemaCompletion.BuildScript(plan));
        var resolved = SchemaCompletion.Compare(meta, "O|EXCEPTION|EX_ABSENT");
        Assert.All(resolved, change => Assert.Null(change.BlockedReason));
    }

    [Fact]
    public void DoesNotReplaceExistingObjects()
    {
        const string meta = "CREATE VIEW V AS SELECT 1 AS ID FROM RDB$DATABASE;";
        Assert.Empty(SchemaCompletion.Compare(meta, "O|VIEW|V"));
    }
}
