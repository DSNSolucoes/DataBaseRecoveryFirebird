using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task PartialCatalogRecoversSupplementaryTablesAndChecksCounts()
    {
        var (files, tools, options) = Arrange();

        var result = await new RecoverDatabase(files, tools).ExecuteAsync(options, _ => { }, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(2, result.Tables);
        Assert.Equal(3, result.Rows);
        Assert.Equal(0, result.Unrecovered);
        Assert.Contains("fbsvcmgr", tools.Calls);
        var report = await files.ReadTextAsync(Path.Combine(result.Folder, "conferencia-dados.csv"), CancellationToken.None);
        Assert.Contains("\"MAIN_T\";3;3;SIM", report);
        Assert.Contains("\"EXTRA_T\";0;0;SIM", report);
        Assert.DoesNotContain("OMIT_T", report);
        var script = await files.ReadTextAsync(Path.Combine(result.Folder, "copiar-tabelas-complementares.sql"), CancellationToken.None);
        Assert.Contains("TYPE OF COLUMN \"EXTRA_T\".\"ID\"", script);
        Assert.Contains("ON EXTERNAL DATA SOURCE", script);
        Assert.Equal(new byte[] { 12, 34 }, files.ReadBytes(options.Source));
    }

    [Theory]
    [InlineData("MAIN_T|2\nEXTRA_T|0", false, 1)]
    [InlineData("MAIN_T|3", false, 1)]
    [InlineData("MAIN_T|3\nEXTRA_T|0", true, 0)]
    public async Task ReportsMismatchesOrStructuralErrors(string found, bool structuralError, int unverified)
    {
        var (files, tools, options) = Arrange();
        tools.DestinationCounts = found;
        tools.StructuralError = structuralError;

        var result = await new RecoverDatabase(files, tools).ExecuteAsync(options, _ => { }, CancellationToken.None);

        Assert.False(result.Verified);
        Assert.Equal(unverified, result.Unrecovered);
    }

    [Fact]
    public async Task AbortsWhenSourceCannotBePutInReadOnlyMode()
    {
        var (files, tools, options) = Arrange();
        tools.ReadOnlyError = true;
        string? folder = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecoverDatabase(files, tools).ExecuteAsync(options, message =>
            {
                const string marker = "Arquivos da execução em ";
                if (message.StartsWith(marker))
                    folder = message[marker.Length..];
            }, CancellationToken.None));

        Assert.NotNull(folder);
        Assert.DoesNotContain("isql", tools.Calls);
        Assert.Contains("Execução interrompida", await files.ReadTextAsync(Path.Combine(folder, "resumo.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDuringCopyDoesNotStartFirebird()
    {
        var (files, tools, options) = Arrange();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RecoverDatabase(files, tools).ExecuteAsync(options, _ => { }, cancelled.Token));

        Assert.Empty(tools.Calls);
        Assert.Equal(new byte[] { 12, 34 }, files.ReadBytes(options.Source));
    }

    private static (MemoryFiles, FakeFirebird, RecoveryOptions) Arrange()
    {
        var files = new MemoryFiles();
        files.CreateDirectory("output");
        files.SetBytes("origin.fdb", [12, 34]);
        return (files, new FakeFirebird(files), new RecoveryOptions("origin.fdb", "output", "firebird", ExcludedNames.Parse("OMIT_T")));
    }

    private sealed class FakeFirebird(MemoryFiles files) : IFirebirdTools
    {
        public List<string> Calls { get; } = [];
        public string DestinationCounts { get; set; } = "MAIN_T|3\nEXTRA_T|0";
        public bool StructuralError { get; set; }
        public bool ReadOnlyError { get; set; }

        public void ValidateInstallation(string directory) => Assert.Equal("firebird", directory);

        public async Task<FirebirdCommandResult> RunAsync(string directory, string executable, IReadOnlyList<string> arguments,
            string? logPath, Action<string>? onLine, CancellationToken cancellation)
        {
            Calls.Add(executable);
            if (arguments.Contains("-x"))
            {
                var outputFile = arguments.ToList().IndexOf("-o") + 1;
                await files.WriteTextAsync(arguments[outputFile], """
                    /* CREATE DATABASE 'origem' DEFAULT CHARACTER SET UTF8; */
                    CREATE TABLE MAIN_T (ID INTEGER);
                    CREATE TABLE OMIT_T (ID INTEGER);
                    """);
                return new FirebirdCommandResult(1, "Statement failed");
            }
            if (executable == "fbsvcmgr")
                return new FirebirdCommandResult(0, "20:00 Relation 500 (EXTRA_T) is ok");
            if (arguments.Contains("read_only"))
                return new FirebirdCommandResult(ReadOnlyError ? 1 : 0, "");
            if (logPath is null)
                return new FirebirdCommandResult(0, "ID    INTEGER Nullable\n");
            if (Path.GetFileName(logPath) == "criar-banco.log")
                files.SetBytes(Path.Combine(Path.GetDirectoryName(logPath)!, "banco-recuperado.fdb"), [0]);
            var response = Path.GetFileName(logPath) switch
            {
                "colunas-destino.log" => "MAIN_T|ID\nEXTRA_T|ID",
                "copiar-dados.log" => "TRANSFERINDO MAIN_T\nLINHAS 3",
                "copiar-tabelas-complementares.log" => "TRANSFERINDO EXTRA_T\nLINHAS 0",
                "verificar-banco.log" => DestinationCounts,
                "validacao-banco-novo.log" when StructuralError => "Number of database page errors: 1",
                _ => ""
            };
            foreach (var line in response.Split('\n'))
                onLine?.Invoke(line);
            return new FirebirdCommandResult(0, response);
        }
    }
}
