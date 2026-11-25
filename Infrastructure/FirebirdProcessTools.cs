using System.Diagnostics;
using System.Text;
using RecuperadorFirebird.Application;

namespace RecuperadorFirebird.Infrastructure;

public sealed class FirebirdProcessTools : IFirebirdTools
{
    public static string FindInstallation()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var candidate = Path.Combine(root, "Firebird", "Firebird_5_0");
            if (File.Exists(Path.Combine(candidate, "gfix.exe")))
                return candidate;
        }
        return "";
    }

    public void ValidateInstallation(string directory)
    {
        foreach (var tool in new[] { "isql", "gfix", "fbsvcmgr", "gstat" })
            if (!File.Exists(Executable(directory, tool)))
                throw new FileNotFoundException($"A pasta selecionada precisa conter {tool}.exe do Firebird 5.");
    }

    public async Task<FirebirdCommandResult> RunAsync(string directory, string executable, IReadOnlyList<string> arguments,
        string? logPath, Action<string>? onLine, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Executable(directory, executable))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        using var writer = logPath is null ? null : new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var output = new StringBuilder();
        process.Start();
        var streams = Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError));
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            await streams;
            throw;
        }
        await streams;
        return new FirebirdCommandResult(process.ExitCode, output.ToString());

        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (output)
                {
                    output.AppendLine(line);
                    writer?.WriteLine(line);
                }
                onLine?.Invoke(line);
            }
        }
    }

    private static string Executable(string directory, string name) => Path.Combine(directory, name + ".exe");
}
