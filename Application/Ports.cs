namespace RecuperadorFirebird.Application;

public interface IWorkspaceFiles
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void DeleteFile(string path);
    byte[] ReadBytes(string path);
    Task<string> ReadTextAsync(string path, CancellationToken cancellation);
    Task WriteTextAsync(string path, string content, bool withBom = false, CancellationToken cancellation = default);
    Task CopyExclusiveAsync(string source, string destination, CancellationToken cancellation);
}

public interface IFirebirdTools
{
    void ValidateInstallation(string directory);

    Task<FirebirdCommandResult> RunAsync(string directory, string executable, IReadOnlyList<string> arguments,
        string? logPath, Action<string>? onLine, CancellationToken cancellation);
}

public sealed record FirebirdCommandResult(int ExitCode, string Output)
{
    public bool Ok => ExitCode == 0 && !System.Text.RegularExpressions.Regex.IsMatch(Output,
        @"(?im)^\s*(Statement failed|gbak: ERROR|I/O error|internal Firebird consistency check)");
}
