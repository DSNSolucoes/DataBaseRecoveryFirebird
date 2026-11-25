using System.Text;
using RecuperadorFirebird.Application;

namespace RecuperadorFirebird.Infrastructure;

public sealed class LocalWorkspaceFiles : IWorkspaceFiles
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);
    public byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    public Task<string> ReadTextAsync(string path, CancellationToken cancellation) =>
        File.ReadAllTextAsync(path, Encoding.UTF8, cancellation);

    public Task WriteTextAsync(string path, string content, bool withBom = false, CancellationToken cancellation = default) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(withBom), cancellation);

    public async Task CopyExclusiveAsync(string source, string destination, CancellationToken cancellation)
    {
        const int bufferSize = 1024 * 1024;
        await using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        await using var copy = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, true);
        await original.CopyToAsync(copy, bufferSize, cancellation);
    }
}
