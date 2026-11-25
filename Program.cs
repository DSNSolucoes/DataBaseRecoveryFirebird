using RecuperadorFirebird.Application;
using RecuperadorFirebird.Domain;
using RecuperadorFirebird.Infrastructure;

namespace RecuperadorFirebird;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var files = new LocalWorkspaceFiles();
        var recover = new RecoverDatabase(files, new FirebirdProcessTools());
        var metadata = new GenerateMetadataScript(files);
        if (args.Length > 0 && args[0] == "--gerar-metadados")
        {
            if (args.Length is < 3 or > 5)
                return 64;
            try
            {
                var selection = new SchemaSelection(ExcludedNames.Parse(args.Length >= 4 ? args[3] : ""),
                    ExcludedNames.Parse(args.Length == 5 ? args[4] : ""));
                var result = metadata.ExecuteAsync(args[1], args[2], selection).GetAwaiter().GetResult();
                Console.WriteLine(result);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        if (args.Length > 0 && args[0] == "--recuperar")
        {
            if (args.Length is not (4 or 5))
                return 64;
            try
            {
                var options = new RecoveryOptions(args[1], args[2], args[3], ExcludedNames.Parse(args.Length == 5 ? args[4] : ""));
                var result = recover.ExecuteAsync(options, Console.WriteLine, CancellationToken.None).GetAwaiter().GetResult();
                return result.Verified ? 0 : 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }
        ApplicationConfiguration.Initialize();
        System.Windows.Forms.Application.Run(new MainForm(recover, metadata, FirebirdProcessTools.FindInstallation()));
        return 0;
    }
}
