using System.Text;
using RecuperadorFirebird.Domain;

namespace RecuperadorFirebird.Application;

public sealed class GenerateMetadataScript(IWorkspaceFiles files)
{
    public async Task<MetadataResult> ExecuteAsync(string input, string output, SchemaSelection selection)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Escolha um arquivo de saída diferente do arquivo de metadados.");

        var generated = MetadataScriptComposer.Compose(Decode(files.ReadBytes(input)), selection);
        await files.WriteTextAsync(output, generated.Content);
        return generated.Counts;
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }
}
