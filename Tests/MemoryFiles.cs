using System.Text;
using RecuperadorFirebird.Application;

namespace RecuperadorFirebird.Tests;

internal sealed class MemoryFiles : IWorkspaceFiles
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => files.ContainsKey(Path.GetFullPath(path));
    public bool DirectoryExists(string path) => directories.Contains(Path.GetFullPath(path));
    public void CreateDirectory(string path) => directories.Add(Path.GetFullPath(path));
    public byte[] ReadBytes(string path) => files[Path.GetFullPath(path)];
    public void SetBytes(string path, byte[] value) => files[Path.GetFullPath(path)] = value;

    public Task<string> ReadTextAsync(string path, CancellationToken cancellation) =>
        Task.FromResult(Encoding.UTF8.GetString(ReadBytes(path)).TrimStart('\uFEFF'));

    public Task WriteTextAsync(string path, string content, bool withBom = false, CancellationToken cancellation = default)
    {
        SetBytes(path, Encoding.UTF8.GetBytes((withBom ? "\uFEFF" : "") + content));
        return Task.CompletedTask;
    }

    public Task CopyExclusiveAsync(string source, string destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        files.Add(Path.GetFullPath(destination), ReadBytes(source).ToArray());
        return Task.CompletedTask;
    }
}
