using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Tests;

public sealed class DomainTests
{
    [Fact]
    public void ExclusionsPreserveCaseOfQuotedNames()
    {
        var names = ExcludedNames.Parse("  tef_arq; \"Nome Misto\"\nTEF_ARQ, \"A\"\"B\" ");

        Assert.Equal(new[] { "A\"B", "Nome Misto", "TEF_ARQ" }, names.Ordered());
        Assert.False(names.Contains("NOME MISTO"));
    }

    [Fact]
    public void SelectionOmitsKeysThatDependOnAnExcludedTable()
    {
        var selection = new SchemaSelection(ExcludedNames.Parse("PARENT"), ExcludedNames.Parse("TR_OMIT, FK_OMIT"));

        Assert.False(selection.IncludesKey("CHILD", "PARENT", "FK_CHILD"));
        Assert.False(selection.IncludesKey("CHILD", "OTHER", "FK_OMIT"));
        Assert.False(selection.IncludesObject("TR_CHILD", "PARENT"));
        Assert.False(selection.IncludesObject("TR_OMIT", "OTHER"));
        Assert.True(selection.IncludesKey("CHILD", "OTHER", "FK_CHILD"));
    }

    [Fact]
    public void ReconciliationDistinguishesZeroRowsFromMissingCounts()
    {
        var result = new RecoveryReconciliation(["EMPTY", "UNREADABLE", "DIFF", "EXCLUDED"],
            new Dictionary<string, long> { ["EMPTY"] = 0, ["DIFF"] = 5, ["EXCLUDED"] = 100 },
            new Dictionary<string, long> { ["EMPTY"] = 0, ["UNREADABLE"] = 0, ["DIFF"] = 4 },
            ExcludedNames.Parse("EXCLUDED"));

        Assert.Equal(2, result.TransferredTables);
        Assert.Equal(5, result.TransferredRows);
        Assert.Equal(2, result.UnverifiedTables);
        Assert.Equal(new[] { "DIFF", "EMPTY", "UNREADABLE" }, result.Tables.Select(table => table.Table));
        Assert.True(result.Tables.Single(table => table.Table == "EMPTY").Matches);
        Assert.Null(result.Tables.Single(table => table.Table == "UNREADABLE").Transferred);
    }
}
