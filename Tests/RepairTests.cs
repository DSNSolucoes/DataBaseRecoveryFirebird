using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Tests;

public sealed class RepairTests
{
    [Theory]
    [InlineData(RepairLevel.Diagnostic, false, false, false)]
    [InlineData(RepairLevel.LogicalRebuild, false, false, false)]
    [InlineData(RepairLevel.PageRepair, true, false, false)]
    [InlineData(RepairLevel.Mend, true, true, false)]
    [InlineData(RepairLevel.IgnoreChecksums, true, true, true)]
    public async Task UsesOnlyTheOptionsOfTheSelectedLevel(RepairLevel level, bool validatesWithUpdates, bool mends, bool ignores)
    {
        var (files, tools, request) = Arrange(level);
        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(level == RepairLevel.Diagnostic ? RepairOutcome.DiagnosticClean : RepairOutcome.RestoredAndValidated, result.Outcome);
        Assert.Equal(validatesWithUpdates, tools.Calls.Any(call => call.Args.Contains("-validate") && !call.Args.Contains("-no_update")));
        Assert.Equal(mends, tools.Calls.Any(call => call.Args.Contains("-mend")));
        Assert.Equal(ignores, tools.Calls.Any(call => call.Args.Contains("-ignore")));
        Assert.DoesNotContain(tools.Calls, call => call.Args.Contains(Path.GetFullPath(request.Source)));
        Assert.Equal(new byte[] { 42 }, files.ReadBytes(request.Source));
        if (level != RepairLevel.Diagnostic)
        {
            Assert.Contains(tools.Calls, call => call.Tool == "gbak" && call.Args.Contains("-create"));
            Assert.NotNull(result.RestoredDatabase);
        }
    }

    [Fact]
    public async Task RequiresConsentBeforeCopyingAtRiskyLevels()
    {
        var (files, tools, request) = Arrange(RepairLevel.Mend);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RepairDatabase(files, tools).ExecuteAsync(request with { AcceptRisk = false }, _ => { }));
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public async Task StopsAtHeaderWhenAnotherDatabaseFileIsReferenced()
    {
        var (files, tools, request) = Arrange(RepairLevel.IgnoreChecksums);
        tools.Header = "Shadow count 0\nNext header page 0\nContinuation file: original-2.fdb\n*END*";

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Equal("gstat", Assert.Single(tools.Calls).Tool);
    }

    [Fact]
    public async Task DoesNotRestoreAPartialBackupEvenIfTheFileExists()
    {
        var (files, tools, request) = Arrange(RepairLevel.LogicalRebuild);
        tools.FailBackup = true;

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(tools.Calls, call => call.Args.Contains("-create"));
        Assert.Null(result.RestoredDatabase);
    }

    [Fact]
    public async Task TreatsValidationOutputAsWarningsEvenWithZeroExitCode()
    {
        var (files, tools, request) = Arrange(RepairLevel.LogicalRebuild);
        tools.Validation = "Number of database page errors: 1";

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(RepairOutcome.RestoredWithWarnings, result.Outcome);
        Assert.NotNull(result.RestoredDatabase);
    }

    [Theory]
    [InlineData("Shadow count 1\nNext header page 0\n*END*")]
    [InlineData("Shadow count 0\nNext header page 12\n*END*")]
    [InlineData("Shadow count 0\nNext header page 0\nDifference file: copy.delta\n*END*")]
    [InlineData("Shadow count 0\nNext header page 0\nAttributes backup lock\n*END*")]
    [InlineData("Shadow count 0\nNext header page 0")]
    public async Task RefusesCopiesWhoseHeadersDoNotEstablishIsolation(string header)
    {
        var (files, tools, request) = Arrange(RepairLevel.Diagnostic);
        tools.Header = header;

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Equal("gstat", Assert.Single(tools.Calls).Tool);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task DoesNotAdvertiseAnIncompleteRestore(int exitCode, bool outputExists)
    {
        var (files, tools, request) = Arrange(RepairLevel.LogicalRebuild);
        tools.RestoreExitCode = exitCode;
        tools.CreateRestoreFile = outputExists;

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Null(result.RestoredDatabase);
        Assert.DoesNotContain(tools.Calls, call => call.Args.Contains("-no_update"));
        Assert.Contains("nenhum confirmado", await files.ReadTextAsync(Path.Combine(result.Folder, "resumo.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task RecordsCancellationAndDoesNotStartRestoration()
    {
        var (files, tools, request) = Arrange(RepairLevel.LogicalRebuild);
        using var cancellation = new CancellationTokenSource();
        tools.BeforeBackup = cancellation.Cancel;

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { }, cancellation.Token);

        Assert.Equal(RepairOutcome.Cancelled, result.Outcome);
        Assert.Null(result.RestoredDatabase);
        Assert.DoesNotContain(tools.Calls, call => call.Args.Contains("-create"));
        Assert.Contains("Execução cancelada", await files.ReadTextAsync(Path.Combine(result.Folder, "resumo.txt"), CancellationToken.None));
        Assert.Equal(new byte[] { 42 }, files.ReadBytes(request.Source));
    }

    [Theory]
    [InlineData(RepairLevel.PageRepair, false)]
    [InlineData(RepairLevel.Mend, true)]
    public async Task FailedStructuralValidationOnlyContinuesToMendWhenSelected(RepairLevel level, bool expectMend)
    {
        var (files, tools, request) = Arrange(level);
        tools.StructuralExitCode = 1;
        tools.MendExitCode = 1;

        var result = await new RepairDatabase(files, tools).ExecuteAsync(request, _ => { });

        Assert.Equal(RepairOutcome.Failed, result.Outcome);
        Assert.Equal(expectMend, tools.Calls.Any(call => call.Args.Contains("-mend")));
        Assert.DoesNotContain(tools.Calls, call => call.Tool == "gbak");
    }

    private static (MemoryFiles, FakeTools, RepairRequest) Arrange(RepairLevel level)
    {
        var files = new MemoryFiles();
        files.CreateDirectory("output");
        files.SetBytes("origin.fdb", [42]);
        files.SetBytes(Path.Combine("firebird", "gbak.exe"), []);
        return (files, new FakeTools(files), new RepairRequest("origin.fdb", "output", "firebird", level, true));
    }

    private sealed class FakeTools(MemoryFiles files) : IFirebirdTools
    {
        public List<(string Tool, IReadOnlyList<string> Args)> Calls { get; } = [];
        public string Header { get; set; } = "Shadow count 0\nNext header page 0\n*END*";
        public string Validation { get; set; } = "";
        public bool FailBackup { get; set; }
        public int RestoreExitCode { get; set; }
        public bool CreateRestoreFile { get; set; } = true;
        public int StructuralExitCode { get; set; }
        public int MendExitCode { get; set; }
        public Action? BeforeBackup { get; set; }

        public void ValidateInstallation(string directory) { }

        public Task<FirebirdCommandResult> RunAsync(string directory, string executable, IReadOnlyList<string> arguments,
            string? logPath, Action<string>? onLine, CancellationToken cancellation)
        {
            Calls.Add((executable, arguments));
            if (executable == "gstat")
                return Task.FromResult(new FirebirdCommandResult(0, Header));
            if (executable == "gbak" && arguments.Contains("-backup"))
            {
                BeforeBackup?.Invoke();
                cancellation.ThrowIfCancellationRequested();
                files.SetBytes(arguments[^1], [1]);
                return Task.FromResult(FailBackup ? new FirebirdCommandResult(1, "gbak: ERROR") : new FirebirdCommandResult(0, ""));
            }
            if (executable == "gbak" && arguments.Contains("-create"))
            {
                if (CreateRestoreFile)
                    files.SetBytes(arguments[^1], [1]);
                return Task.FromResult(new FirebirdCommandResult(RestoreExitCode, ""));
            }
            if (arguments.Contains("-mend"))
                return Task.FromResult(new FirebirdCommandResult(MendExitCode, ""));
            if (arguments.Contains("-no_update"))
                return Task.FromResult(new FirebirdCommandResult(0, Validation));
            return Task.FromResult(new FirebirdCommandResult(StructuralExitCode, ""));
        }
    }
}
