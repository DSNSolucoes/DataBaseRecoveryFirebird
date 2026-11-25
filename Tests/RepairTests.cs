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

        public void ValidateInstallation(string directory) { }

        public Task<FirebirdCommandResult> RunAsync(string directory, string executable, IReadOnlyList<string> arguments,
            string? logPath, Action<string>? onLine, CancellationToken cancellation)
        {
            Calls.Add((executable, arguments));
            if (executable == "gstat")
                return Task.FromResult(new FirebirdCommandResult(0, Header));
            if (executable == "gbak")
                files.SetBytes(arguments[^1], [1]);
            if (executable == "gbak" && arguments.Contains("-backup") && FailBackup)
                return Task.FromResult(new FirebirdCommandResult(1, "gbak: ERROR"));
            return Task.FromResult(new FirebirdCommandResult(0, arguments.Contains("-no_update") ? Validation : ""));
        }
    }
}
